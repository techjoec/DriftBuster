namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// Derived from CPython 3.13's <c>re._compiler._code</c> / <c>_compile</c> (PSF License): the same opcode layout, skip offsets and
/// flag handling, so <see cref="ReMatcher"/> can run the code exactly as <c>_sre</c> does.
/// </summary>
/// <remarks>
/// Single-character nodes compile to <see cref="ReOpcode.Char"/> over the code point set <c>_compile</c> and
/// <c>_optimize_charset</c> would make the <c>LITERAL</c> / <c>IN</c> / <c>ANY</c> opcode accept. The <c>INFO</c> block's literal
/// prefix and charset only speed up <c>_sre</c>'s search; the compiler keeps its minimum width and an equivalent first-set filter
/// (<see cref="ReProgram.FirstSet"/>).
/// </remarks>
internal sealed class ReCompiler
{
    private const long MaxCode = uint.MaxValue;

    private readonly List<long> _code = [];
    private readonly List<ReCharSet> _sets = [];

    /// <summary><c>_code(p, flags)</c>.</summary>
    public static ReProgram Compile(ReSubPattern pattern, EngineReFlags flags)
    {
        var compiler = new ReCompiler();
        flags |= pattern.State.Flags;
        var positionalTails = new HashSet<int>();
        var nodes = pattern.Nodes;
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is ReNode.Repeat { Kind: RepeatKind.Greedy } repeat && IsSimple(repeat.Item)
                && nodes.Skip(index + 1).All(IsPositional))
            {
                // The REPEAT_ONE opcode is emitted next; its skip operand follows it.
                positionalTails.Add(compiler._code.Count + 1);
            }

            compiler.CompileNode(nodes[index], flags);
        }

        compiler.Emit(ReOpcode.Success);
        var minWidth = pattern.GetWidth().Low;
        return new ReProgram(
            [.. compiler._code],
            [.. compiler._sets],
            minWidth < MaxCode ? (long)minWidth : MaxCode,
            pattern.State.Groups - 1,
            ReFirstSet.Of(pattern, flags),
            positionalTails);
    }

    // A tail node whose outcome depends only on the text position it starts from: single characters, assertions on the
    // surrounding characters (\b, ^, $), single-character repeats, and groups and alternations made of those. Groups in a tail
    // open after every group of the head, so a failed tail's marks lie above the restored lastmark and are never observable
    // (MARK clears the marks it skips when it raises lastmark). No group reference, no conditional, no look-around or atomic
    // group and no repeat context, so a tail that failed from a position fails from it for every start.
    private static bool IsPositional(ReNode node) => node switch
    {
        ReNode.Literal or ReNode.In or ReNode.Any or ReNode.At => true,
        ReNode.Repeat repeat => IsSimple(repeat.Item),
        ReNode.Subpattern group => group.Body.Nodes.All(IsPositional),
        ReNode.Branch branch => branch.Alternatives.All(alternative => alternative.Nodes.All(IsPositional)),
        _ => false,
    };

    private void Emit(ReOpcode op) => _code.Add((long)op);

    private void Emit(long value) => _code.Add(value);

    private int EmitSkip()
    {
        _code.Add(0);
        return _code.Count - 1;
    }

    private void FixSkip(int skip, int extra = 0) => _code[skip] = _code.Count - skip + extra;

    private void CompileSequence(ReSubPattern pattern, EngineReFlags flags)
    {
        foreach (var node in pattern.Nodes)
        {
            CompileNode(node, flags);
        }
    }

    private void CompileNode(ReNode node, EngineReFlags flags)
    {
        switch (node)
        {
            case ReNode.Literal or ReNode.In or ReNode.Any:
                Emit(ReOpcode.Char);
                Emit(_sets.Count);
                _sets.Add(new ReCharSet(ReCharacterSets.NodeSet(node, flags)));
                break;
            case ReNode.Repeat repeat:
                CompileRepeat(repeat, flags);
                break;
            case ReNode.Subpattern group:
                CompileGroup(group, flags);
                break;
            case ReNode.Atomic atomic:
                Emit(ReOpcode.AtomicGroup);
                var skip = EmitSkip();
                CompileSequence(atomic.Body, flags);
                Emit(ReOpcode.Success);
                FixSkip(skip);
                break;
            case ReNode.Failure:
                Emit(ReOpcode.Failure);
                break;
            case ReNode.Assert assertion:
                CompileAssert(assertion, flags);
                break;
            case ReNode.At at:
                Emit(ReOpcode.At);
                Emit((long)AtCodeFor(at.Code, flags));
                break;
            case ReNode.Branch branch:
                CompileBranch(branch, flags);
                break;
            default:
                CompileReference(node, flags);
                break;
        }
    }

    private void CompileRepeat(ReNode.Repeat repeat, EngineReFlags flags)
    {
        var simple = IsSimple(repeat.Item);
        var op = (repeat.Kind, simple) switch
        {
            (RepeatKind.Greedy, true) => ReOpcode.RepeatOne,
            (RepeatKind.Lazy, true) => ReOpcode.MinRepeatOne,
            (RepeatKind.Possessive, true) => ReOpcode.PossessiveRepeatOne,
            (RepeatKind.Possessive, false) => ReOpcode.PossessiveRepeat,
            _ => ReOpcode.Repeat,
        };
        Emit(op);
        var skip = EmitSkip();
        Emit(repeat.Min);
        Emit(repeat.Max);
        CompileSequence(repeat.Item, flags);
        if (simple)
        {
            Emit(ReOpcode.Success);
            FixSkip(skip);
            return;
        }

        FixSkip(skip);
        Emit(repeat.Kind switch
        {
            RepeatKind.Greedy => ReOpcode.MaxUntil,
            RepeatKind.Lazy => ReOpcode.MinUntil,
            _ => ReOpcode.Success,
        });
    }

    // _simple(p): one unit opcode, possibly inside non-capturing groups.
    private static bool IsSimple(ReSubPattern pattern)
    {
        if (pattern.Nodes.Count != 1)
        {
            return false;
        }

        return pattern.Nodes[0] switch
        {
            ReNode.Subpattern group => group.Group is null && IsSimple(group.Body),
            ReNode.Literal or ReNode.In or ReNode.Any => true,
            _ => false,
        };
    }

    private void CompileGroup(ReNode.Subpattern group, EngineReFlags flags)
    {
        if (group.Group is { } number)
        {
            Emit(ReOpcode.Mark);
            Emit((number - 1) * 2);
        }

        CompileSequence(group.Body, CombineFlags(flags, group.AddFlags, group.DelFlags));
        if (group.Group is { } closing)
        {
            Emit(ReOpcode.Mark);
            Emit(((closing - 1) * 2) + 1);
        }
    }

    private void CompileAssert(ReNode.Assert assertion, EngineReFlags flags)
    {
        Emit(assertion.Negative ? ReOpcode.AssertNot : ReOpcode.Assert);
        var skip = EmitSkip();
        if (!assertion.Behind)
        {
            Emit(0L);
        }
        else
        {
            var (low, high) = assertion.Body.GetWidth();
            if (low > MaxCode)
            {
                throw new EngineReException("looks too much behind", position: null);
            }

            if (low != high)
            {
                throw new EngineReException("look-behind requires fixed-width pattern", position: null);
            }

            Emit((long)low);
        }

        CompileSequence(assertion.Body, flags);
        Emit(ReOpcode.Success);
        FixSkip(skip);
    }

    private void CompileBranch(ReNode.Branch branch, EngineReFlags flags)
    {
        Emit(ReOpcode.Branch);
        var tails = new List<int>();
        foreach (var alternative in branch.Alternatives)
        {
            var skip = EmitSkip();
            CompileSequence(alternative, flags);
            Emit(ReOpcode.Jump);
            tails.Add(EmitSkip());
            FixSkip(skip);
        }

        Emit(ReOpcode.Failure);
        foreach (var tail in tails)
        {
            FixSkip(tail);
        }
    }

    private void CompileReference(ReNode node, EngineReFlags flags)
    {
        if (node is ReNode.GroupRef reference)
        {
            Emit(!flags.HasFlag(EngineReFlags.IgnoreCase) ? ReOpcode.GroupRef
                : flags.HasFlag(EngineReFlags.Unicode) ? ReOpcode.GroupRefUniIgnore : ReOpcode.GroupRefIgnore);
            Emit(reference.Group - 1);
            return;
        }

        var conditional = (ReNode.GroupRefExists)node;
        Emit(ReOpcode.GroupRefExists);
        Emit(conditional.Group - 1);
        var skipYes = EmitSkip();
        CompileSequence(conditional.Yes, flags);
        if (conditional.No is null)
        {
            FixSkip(skipYes, 1);
            return;
        }

        Emit(ReOpcode.Jump);
        var skipNo = EmitSkip();
        FixSkip(skipYes, 1);
        CompileSequence(conditional.No, flags);
        FixSkip(skipNo);
    }

    private static ReAtCode AtCodeFor(AtCode code, EngineReFlags flags)
    {
        var multiline = flags.HasFlag(EngineReFlags.Multiline);
        var unicode = flags.HasFlag(EngineReFlags.Unicode);
        return code switch
        {
            AtCode.Beginning => multiline ? ReAtCode.BeginningLine : ReAtCode.Beginning,
            AtCode.End => multiline ? ReAtCode.EndLine : ReAtCode.End,
            AtCode.BeginningString => ReAtCode.BeginningString,
            AtCode.EndString => ReAtCode.EndString,
            AtCode.Boundary => unicode ? ReAtCode.UniBoundary : ReAtCode.Boundary,
            _ => unicode ? ReAtCode.UniNonBoundary : ReAtCode.NonBoundary,
        };
    }

    /// <summary><c>_combine_flags</c>.</summary>
    internal static EngineReFlags CombineFlags(EngineReFlags flags, EngineReFlags add, EngineReFlags del)
    {
        const EngineReFlags typeFlags = EngineReFlags.Ascii | EngineReFlags.Locale | EngineReFlags.Unicode;
        if ((add & typeFlags) != EngineReFlags.None)
        {
            flags &= ~typeFlags;
        }

        return (flags | add) & ~del;
    }
}
