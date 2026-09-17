namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// Derived from CPython 3.13's <c>SRE(match)</c> and <c>SRE(search)</c> (<c>Modules/_sre/sre_lib.h</c>, PSF License) over a code
/// point array: the same opcode semantics, backtracking order, mark stack, repeat contexts, zero-width iteration guards and
/// <c>lastindex</c> bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// <c>_sre</c> runs its opcode loop on an explicit context stack: a <c>DO_JUMP</c> saves the current pattern and text position
/// in the context, pushes a child context and dispatches the child's code; when the child returns, the parent resumes at the
/// label named by the child's jump id. <see cref="Run"/> does the same with <see cref="Frame"/> objects, each opcode handler
/// returning <see cref="Signal.Dispatch"/> (next opcode), <see cref="Signal.Success"/> or <see cref="Signal.Failure"/> (leave
/// the context), and <see cref="Resume"/> continuing the handler after its child. Marks and <c>last_ptr</c> values are saved on
/// a separate data stack in the same push and pop order as <c>DATA_STACK_PUSH</c> / <c>DATA_STACK_POP</c>.
/// </para>
/// <para>
/// Three pieces of work are reused within one subject, none of which changes what any opcode sees: the end of the last unbounded
/// single-character run counted for a set (a later start inside that run ends at the same place), the last backward scan for a
/// tail's first character (a narrower window below the same bound needs no rescan), and, for a top-level greedy
/// single-character repeat whose tail succeeds or fails by position alone (<see cref="ReProgram.PositionalTails"/>), the
/// positions its tail already failed from outside any repeat context. A long line where a <c>[^\n]*</c> before a literal is
/// retried from thousands of start positions therefore costs linear work instead of quadratic, with <c>_sre</c>'s results.
/// </para>
/// <para>
/// Python has no match time limit and neither does the matcher, so a slow search finishes with Python's result however long
/// it takes. The caller's <see cref="CancellationToken"/> is polled every 4096 opcodes instead, and a cancelled search throws
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
internal sealed partial class ReMatcher
{
    private const long MaxRepeat = ReNode.MaxRepeat;
    private const int NoPosition = -1;

    private readonly ReProgram _program;
    private readonly long[] _code;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Frame> _frames = [];
    private int _depth;
    private int[] _data = new int[64];
    private int _dataTop;
    private Frame _ctx = null!;
    private int _pc;
    private int _ptr;
    private long _ticks;
    private long _work;

    // Per positional tail (keyed by its REPEAT_ONE skip position), over the current Text only: the failure links of
    // RepeatOneNext, and the failed positions one backtracking loop passed over.
    private const int NotFailed = int.MaxValue;
    private readonly List<int> _failedChain = [];
    private Dictionary<int, int[]>? _tailFailures;

    // Per character set, over the current Text only: the last unbounded run counted and the last backward member scan.
    private readonly (int From, int To)[] _runCache;
    private readonly (int Low, int High, int Found)[] _scanCache;

    public ReMatcher(ReProgram program, CancellationToken cancellationToken)
    {
        _program = program;
        _code = program.Code;
        _cancellationToken = cancellationToken;
        Marks = new int[Math.Max(1, program.Groups * 2)];
        _runCache = new (int From, int To)[program.Sets.Length];
        _scanCache = new (int Low, int High, int Found)[program.Sets.Length];
        ClearScanCaches();
    }

    private enum Signal
    {
        Dispatch,
        Success,
        Failure,
    }

    /// <summary>
    /// Opcodes dispatched plus characters examined by the single-character run and member scans since construction: a measure of
    /// search work that does not depend on host speed.
    /// </summary>
    internal long Work => _work + _ticks;

    /// <summary>The subject as code points.</summary>
    public int[] Text { get; private set; } = [];

    /// <summary><c>state->start</c>.</summary>
    public int Start { get; private set; }

    /// <summary><c>state->ptr</c>.</summary>
    public int Ptr { get; private set; }

    /// <summary><c>state->mark</c>.</summary>
    public int[] Marks { get; }

    /// <summary><c>state->lastmark</c>.</summary>
    public int LastMark { get; private set; } = -1;

    /// <summary><c>state->lastindex</c>.</summary>
    public int LastIndex { get; private set; } = -1;

    private int End => Text.Length;

    private bool MatchAll { get; set; }

    private bool MustAdvance { get; set; }

    private RepeatContext? Repeat { get; set; }

    /// <summary><c>state_init</c> over the code points <paramref name="text"/>, starting at code point <paramref name="start"/>.</summary>
    public void Reset(int[] text, int start)
    {
        Text = text;
        Start = start;
        Ptr = start;
        MatchAll = false;
        MustAdvance = false;
        ClearScanCaches();
        StateReset();
    }

    private void ClearScanCaches()
    {
        Array.Fill(_runCache, (-1, -1));
        Array.Fill(_scanCache, (-1, -1, -1));
        _tailFailures = null;
    }

    // state_reset: lastmark, lastindex, repeat and the data stack.
    private void StateReset()
    {
        LastMark = -1;
        LastIndex = -1;
        Repeat = null;
        _dataTop = 0;
        _depth = 0;
    }

    private void BeginRun()
    {
        _work += _ticks;
        _ticks = 0;
        _cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary><c>SRE(match)(state, pattern, toplevel)</c>: true on a match, with <see cref="Ptr"/> at its end.</summary>
    private bool Run(int pc, bool toplevel)
    {
        var baseDepth = _depth;
        PushFrame(JumpId.None, toplevel);
        _pc = pc;
        _ptr = Ptr;
        var signal = Signal.Dispatch;
        while (true)
        {
            while (signal == Signal.Dispatch)
            {
                if ((++_ticks & 0xFFF) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                signal = Execute((ReOpcode)_code[_pc++]);
            }

            var succeeded = signal == Signal.Success;
            var jump = _ctx.Jump;
            _depth--;
            if (_depth == baseDepth)
            {
                return succeeded;
            }

            _ctx = _frames[_depth - 1];
            _pc = _ctx.Pc;
            _ptr = _ctx.Ptr;
            signal = Resume(jump, succeeded);
        }
    }

    private void PushFrame(JumpId jump, bool toplevel)
    {
        if (_depth == _frames.Count)
        {
            _frames.Add(new Frame());
        }

        var frame = _frames[_depth++];
        frame.Jump = jump;
        frame.TopLevel = toplevel;
        frame.Count = 0;
        frame.Chr = -1;
        frame.Rep = null;
        frame.LastMark = 0;
        frame.LastIndex = 0;
        _ctx = frame;
    }

    // DO_JUMPX: save the current position in the context, enter a child context at next with state->ptr as its position.
    private Signal DoJump(JumpId jump, int next, bool toplevel)
    {
        _ctx.Pc = _pc;
        _ctx.Ptr = _ptr;
        PushFrame(jump, toplevel);
        _pc = next;
        _ptr = Ptr;
        return Signal.Dispatch;
    }

    private Signal DoJump(JumpId jump, int next) => DoJump(jump, next, _ctx.TopLevel);

    private Signal Execute(ReOpcode op) => op switch
    {
        ReOpcode.Mark => OpMark(),
        ReOpcode.Char => OpChar(),
        ReOpcode.Success => OpSuccess(),
        ReOpcode.At => OpAt(),
        ReOpcode.Jump => OpJump(),
        ReOpcode.Branch => OpBranch(),
        ReOpcode.RepeatOne => OpRepeatOne(),
        ReOpcode.MinRepeatOne => OpMinRepeatOne(),
        ReOpcode.PossessiveRepeatOne => OpPossessiveRepeatOne(),
        ReOpcode.Repeat => OpRepeat(),
        ReOpcode.MaxUntil => OpMaxUntil(),
        ReOpcode.MinUntil => OpMinUntil(),
        ReOpcode.PossessiveRepeat => OpPossessiveRepeat(),
        ReOpcode.AtomicGroup => OpAtomicGroup(),
        ReOpcode.GroupRef or ReOpcode.GroupRefIgnore or ReOpcode.GroupRefUniIgnore => OpGroupRef(op),
        ReOpcode.GroupRefExists => OpGroupRefExists(),
        ReOpcode.Assert => OpAssert(),
        ReOpcode.AssertNot => OpAssertNot(),
        _ => Signal.Failure,
    };

    private Signal Resume(JumpId jump, bool succeeded) => jump switch
    {
        JumpId.Branch => ResumeBranch(succeeded),
        JumpId.RepeatOne => ResumeRepeatOne(succeeded),
        JumpId.MinRepeatOne => ResumeMinRepeatOne(succeeded),
        JumpId.Repeat => ResumeRepeat(succeeded),
        JumpId.MaxUntil1 or JumpId.MinUntil1 => ResumeUntilMinimum(succeeded),
        JumpId.MaxUntil2 => ResumeMaxUntilItem(succeeded),
        JumpId.MaxUntil3 => ResumeMaxUntilTail(succeeded),
        JumpId.MinUntil2 => ResumeMinUntilTail(succeeded),
        JumpId.MinUntil3 => ResumeMinUntilItem(succeeded),
        JumpId.PossessiveRepeat1 => ResumePossessiveMinimum(succeeded),
        JumpId.PossessiveRepeat2 => ResumePossessiveMore(succeeded),
        JumpId.AtomicGroup => ResumeAtomicGroup(succeeded),
        JumpId.Assert => ResumeAssert(succeeded),
        _ => ResumeAssertNot(succeeded),
    };

    // LASTMARK_SAVE / LASTMARK_RESTORE.
    private void LastMarkSave()
    {
        _ctx.LastMark = LastMark;
        _ctx.LastIndex = LastIndex;
    }

    private void LastMarkRestore()
    {
        LastMark = _ctx.LastMark;
        LastIndex = _ctx.LastIndex;
    }

    // MARK_PUSH / MARK_POP / MARK_POP_KEEP / MARK_POP_DISCARD over marks[0..lastmark].
    private void MarkPush(int lastMark)
    {
        if (lastMark < 0)
        {
            return;
        }

        EnsureData(lastMark + 1);
        Array.Copy(Marks, 0, _data, _dataTop, lastMark + 1);
        _dataTop += lastMark + 1;
    }

    private void MarkPop(int lastMark, bool keep = false)
    {
        if (lastMark < 0)
        {
            return;
        }

        Array.Copy(_data, _dataTop - (lastMark + 1), Marks, 0, lastMark + 1);
        if (!keep)
        {
            _dataTop -= lastMark + 1;
        }
    }

    private void MarkPopDiscard(int lastMark)
    {
        if (lastMark >= 0)
        {
            _dataTop -= lastMark + 1;
        }
    }

    // LAST_PTR_PUSH / LAST_PTR_POP.
    private void LastPtrPush(RepeatContext repeat)
    {
        EnsureData(1);
        _data[_dataTop++] = repeat.LastPtr;
    }

    private void LastPtrPop(RepeatContext repeat) => repeat.LastPtr = _data[--_dataTop];

    private void EnsureData(int size)
    {
        if (_dataTop + size > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _dataTop + size));
        }
    }

    private sealed class Frame
    {
        public int Pc { get; set; }

        public int Ptr { get; set; }

        public long Count { get; set; }

        public int Chr { get; set; }

        public RepeatContext? Rep { get; set; }

        public int LastMark { get; set; }

        public int LastIndex { get; set; }

        public bool TopLevel { get; set; }

        public JumpId Jump { get; set; }
    }

    // SRE_REPEAT.
    private sealed class RepeatContext
    {
        public long Count { get; set; }

        public int Pattern { get; set; }

        public RepeatContext? Prev { get; set; }

        public int LastPtr { get; set; }
    }

    private enum JumpId
    {
        None,
        MaxUntil1,
        MaxUntil2,
        MaxUntil3,
        MinUntil1,
        MinUntil2,
        MinUntil3,
        Repeat,
        RepeatOne,
        MinRepeatOne,
        Branch,
        Assert,
        AssertNot,
        PossessiveRepeat1,
        PossessiveRepeat2,
        AtomicGroup,
    }
}
