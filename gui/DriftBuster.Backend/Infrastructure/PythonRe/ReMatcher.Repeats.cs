namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>The single-character repeat opcodes of <c>SRE(match)</c> and <c>SRE(count)</c>.</summary>
internal sealed partial class ReMatcher
{
    // SRE(count) for a single-character item (always one CHAR opcode here), from state->ptr, at most maxCount times. An
    // unbounded count reuses the run found last time for the same set when the start lies inside it: every position from the
    // run's start to its end is in the set and the end is not, so the run from any later start inside it ends there too.
    private int CountItem(int itemPc, long maxCount)
    {
        var setIndex = (int)_code[itemPc + 1];
        var set = _program.Sets[setIndex];
        var start = Ptr;
        if (maxCount < End - start && maxCount != MaxRepeat)
        {
            var bounded = set.CountRun(Text.AsSpan(start, (int)maxCount));
            _work += bounded;
            return bounded;
        }

        ref var run = ref _runCache[setIndex];
        if (!(run.From >= 0 && run.From <= start && start <= run.To))
        {
            run = (start, start + set.CountRun(Text.AsSpan(start, End - start)));
            _work += run.To - start;
        }

        return run.To - start;
    }

    // The last position in [low, high] holding a member of set setIndex, or -1. The previous answer for the same set and the same
    // high bound is reused: it is the last member at or below high down to its low bound, so a narrower window needs no scan
    // and a wider one only scans the part below the old low bound. A greedy repeat that backtracks from the same run end for
    // many start positions (a [^\n]* before a literal on a long line) then scans each position once, not once per start.
    private int LastMember(int setIndex, int low, int high)
    {
        if (high < low)
        {
            return -1;
        }

        ref var scan = ref _scanCache[setIndex];
        if (scan.Low >= 0 && scan.High == high)
        {
            if (scan.Found >= low)
            {
                return scan.Found;
            }

            if (scan.Found >= 0 || low >= scan.Low)
            {
                return -1;
            }

            var below = _program.Sets[setIndex].LastIndexIn(Text.AsSpan(low, scan.Low - low));
            _work += below >= 0 ? scan.Low - low - below : scan.Low - low;
            scan = (low, high, below >= 0 ? low + below : -1);
            return scan.Found;
        }

        var found = _program.Sets[setIndex].LastIndexIn(Text.AsSpan(low, high - low + 1));
        _work += found >= 0 ? high - low + 1 - found : high - low + 1;
        scan = (low, high, found >= 0 ? low + found : -1);
        return scan.Found;
    }

    // <REPEAT_ONE> <skip> <1=min> <2=max> item <SUCCESS> tail
    private Signal OpRepeatOne()
    {
        var min = _code[_pc + 1];
        if (min > End - _ptr)
        {
            return Signal.Failure;
        }

        Ptr = _ptr;
        _ctx.Count = CountItem(_pc + 3, _code[_pc + 2]);
        _ptr += (int)_ctx.Count;
        if (_ctx.Count < min)
        {
            return Signal.Failure;
        }

        var tail = _pc + (int)_code[_pc];
        if ((ReOpcode)_code[tail] == ReOpcode.Success && _ptr == End && !(_ctx.TopLevel && MustAdvance && _ptr == Start))
        {
            Ptr = _ptr;
            return Signal.Success;
        }

        LastMarkSave();
        if (Repeat is not null)
        {
            MarkPush(_ctx.LastMark);
        }

        // The tail-starts-with-a-literal loop of SRE_OP_REPEAT_ONE: positions where the tail's first character cannot match
        // are passed over without entering the tail.
        _ctx.Chr = (ReOpcode)_code[tail] == ReOpcode.Char ? (int)_code[tail + 1] : -1;
        return RepeatOneNext();
    }

    private Signal RepeatOneNext()
    {
        var min = _code[_pc + 1];
        var failures = Repeat is null ? TailFailures(create: false) : null;
        _failedChain.Clear();
        while (true)
        {
            var basePtr = _ptr - (int)_ctx.Count;
            if (_ctx.Chr >= 0 && _ctx.Count >= min)
            {
                // Step back while the count allows and the character at ptr (none at the end) is outside the tail's first set:
                // the last position in [base + min, ptr] holding a member, or base + min - 1 when there is none.
                var found = LastMember(_ctx.Chr, basePtr + (int)min, Math.Min(_ptr, End - 1));
                _ptr = found >= 0 ? found : basePtr + (int)min - 1;
                _ctx.Count = _ptr - basePtr;
            }

            if (_ctx.Count < min)
            {
                LinkFailedChain(failures, basePtr + (int)min - 1);
                if (Repeat is not null)
                {
                    MarkPopDiscard(_ctx.LastMark);
                }

                return Signal.Failure;
            }

            if (failures is not null && failures[_ptr] != NotFailed)
            {
                // The tail already failed from here: do what ResumeRepeatOne does after a failure (no repeat context, so no mark
                // to pop) and continue below every position the failure links say failed too.
                LastMarkRestore();
                _failedChain.Add(_ptr);
                _ptr = Math.Max(failures[_ptr], basePtr - 1);
                _ctx.Count = _ptr - basePtr;
                continue;
            }

            LinkFailedChain(failures, _ptr);
            Ptr = _ptr;
            return DoJump(JumpId.RepeatOne, _pc + (int)_code[_pc]);
        }
    }

    // A positional tail (ReProgram.PositionalTails) outside any repeat context fails or succeeds from a position of the current
    // text whatever the start. For each such tail, failures[p] is NotFailed, or (the tail failed from p) a lower position q such
    // that the tail failed from every position in (q, p] it could be entered from: the backtracking loop jumps from p to q
    // instead of entering the tail again, and every position passed over on the way is linked straight to where it stopped.
    private int[]? TailFailures(bool create)
    {
        if (!_program.PositionalTails.Contains(_pc))
        {
            return null;
        }

        if (_tailFailures is not null && _tailFailures.TryGetValue(_pc, out var failures))
        {
            return failures;
        }

        if (!create)
        {
            return null;
        }

        failures = new int[End + 1];
        Array.Fill(failures, NotFailed);
        (_tailFailures ??= [])[_pc] = failures;
        return failures;
    }

    private void LinkFailedChain(int[]? failures, int target)
    {
        if (failures is null)
        {
            return;
        }

        foreach (var position in _failedChain)
        {
            failures[position] = Math.Min(failures[position], target);
        }
    }

    // Recorded after the tail failed from position; not where the top-level must-advance rule could have caused the failure
    // (a tail entered at the search start), since that failure is not a property of the position alone.
    private void RecordTailFailure(int position)
    {
        if (Repeat is not null || (MustAdvance && position == Start))
        {
            return;
        }

        var failures = TailFailures(create: true);
        if (failures is not null && failures[position] == NotFailed)
        {
            failures[position] = position - 1;
        }
    }

    private Signal ResumeRepeatOne(bool succeeded)
    {
        if (succeeded)
        {
            if (Repeat is not null)
            {
                MarkPopDiscard(_ctx.LastMark);
            }

            return Signal.Success;
        }

        if (Repeat is not null)
        {
            MarkPop(_ctx.LastMark, keep: true);
        }

        LastMarkRestore();
        RecordTailFailure(_ptr);
        _ptr--;
        _ctx.Count--;
        return RepeatOneNext();
    }

    // <MIN_REPEAT_ONE> <skip> <1=min> <2=max> item <SUCCESS> tail
    private Signal OpMinRepeatOne()
    {
        var min = _code[_pc + 1];
        if (min > End - _ptr)
        {
            return Signal.Failure;
        }

        Ptr = _ptr;
        if (min == 0)
        {
            _ctx.Count = 0;
        }
        else
        {
            var count = CountItem(_pc + 3, min);
            if (count < min)
            {
                return Signal.Failure;
            }

            _ctx.Count = count;
            _ptr += count;
        }

        var tail = _pc + (int)_code[_pc];
        if ((ReOpcode)_code[tail] == ReOpcode.Success
            && !(_ctx.TopLevel && ((MatchAll && _ptr != End) || (MustAdvance && _ptr == Start))))
        {
            Ptr = _ptr;
            return Signal.Success;
        }

        LastMarkSave();
        if (Repeat is not null)
        {
            MarkPush(_ctx.LastMark);
        }

        return MinRepeatOneNext();
    }

    private Signal MinRepeatOneNext()
    {
        var max = _code[_pc + 2];
        if (max == MaxRepeat || _ctx.Count <= max)
        {
            Ptr = _ptr;
            return DoJump(JumpId.MinRepeatOne, _pc + (int)_code[_pc]);
        }

        if (Repeat is not null)
        {
            MarkPopDiscard(_ctx.LastMark);
        }

        return Signal.Failure;
    }

    private Signal ResumeMinRepeatOne(bool succeeded)
    {
        if (succeeded)
        {
            if (Repeat is not null)
            {
                MarkPopDiscard(_ctx.LastMark);
            }

            return Signal.Success;
        }

        if (Repeat is not null)
        {
            MarkPop(_ctx.LastMark, keep: true);
        }

        LastMarkRestore();
        Ptr = _ptr;
        if (CountItem(_pc + 3, 1) == 0)
        {
            if (Repeat is not null)
            {
                MarkPopDiscard(_ctx.LastMark);
            }

            return Signal.Failure;
        }

        _ptr++;
        _ctx.Count++;
        return MinRepeatOneNext();
    }

    // <POSSESSIVE_REPEAT_ONE> <skip> <1=min> <2=max> item <SUCCESS> tail
    private Signal OpPossessiveRepeatOne()
    {
        var min = _code[_pc + 1];
        if (_ptr + min > End)
        {
            return Signal.Failure;
        }

        Ptr = _ptr;
        _ctx.Count = CountItem(_pc + 3, _code[_pc + 2]);
        _ptr += (int)_ctx.Count;
        if (_ctx.Count < min)
        {
            return Signal.Failure;
        }

        _pc += (int)_code[_pc];
        if ((ReOpcode)_code[_pc] == ReOpcode.Success && _ptr == End && !(_ctx.TopLevel && MustAdvance && _ptr == Start))
        {
            Ptr = _ptr;
            return Signal.Success;
        }

        return Signal.Dispatch;
    }
}
