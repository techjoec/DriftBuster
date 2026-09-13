namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>
/// The general repeat opcodes of <c>SRE(match)</c>: <c>REPEAT</c> with <c>MAX_UNTIL</c> / <c>MIN_UNTIL</c>, and
/// <c>POSSESSIVE_REPEAT</c>, including the <c>last_ptr</c> zero-width match protection that stops an iteration which consumed
/// nothing from starting another.
/// </summary>
internal sealed partial class ReMatcher
{
    // <REPEAT> <skip> <1=min> <2=max> item <UNTIL> tail
    private Signal OpRepeat()
    {
        var repeat = new RepeatContext { Count = -1, Pattern = _pc, Prev = Repeat, LastPtr = NoPosition };
        _ctx.Rep = repeat;
        Repeat = repeat;
        Ptr = _ptr;
        return DoJump(JumpId.Repeat, _pc + (int)_code[_pc]);
    }

    private Signal ResumeRepeat(bool succeeded)
    {
        Repeat = _ctx.Rep!.Prev;
        return succeeded ? Signal.Success : Signal.Failure;
    }

    private RepeatContext EnterUntil()
    {
        var repeat = Repeat ?? throw new InvalidOperationException("internal error in regular expression engine");
        _ctx.Rep = repeat;
        Ptr = _ptr;
        _ctx.Count = repeat.Count + 1;
        return repeat;
    }

    private Signal OpMaxUntil()
    {
        var repeat = EnterUntil();
        if (_ctx.Count < _code[repeat.Pattern + 1])
        {
            repeat.Count = _ctx.Count;
            return DoJump(JumpId.MaxUntil1, repeat.Pattern + 3);
        }

        var max = _code[repeat.Pattern + 2];
        if ((_ctx.Count < max || max == MaxRepeat) && Ptr != repeat.LastPtr)
        {
            repeat.Count = _ctx.Count;
            LastMarkSave();
            MarkPush(_ctx.LastMark);
            LastPtrPush(repeat);
            repeat.LastPtr = Ptr;
            return DoJump(JumpId.MaxUntil2, repeat.Pattern + 3);
        }

        return MaxUntilTail();
    }

    // Both UNTIL opcodes: the item run that was needed to reach the minimum count.
    private Signal ResumeUntilMinimum(bool succeeded)
    {
        if (succeeded)
        {
            return Signal.Success;
        }

        _ctx.Rep!.Count = _ctx.Count - 1;
        Ptr = _ptr;
        return Signal.Failure;
    }

    private Signal ResumeMaxUntilItem(bool succeeded)
    {
        var repeat = _ctx.Rep!;
        LastPtrPop(repeat);
        if (succeeded)
        {
            MarkPopDiscard(_ctx.LastMark);
            return Signal.Success;
        }

        MarkPop(_ctx.LastMark);
        LastMarkRestore();
        repeat.Count = _ctx.Count - 1;
        Ptr = _ptr;
        return MaxUntilTail();
    }

    private Signal MaxUntilTail()
    {
        Repeat = _ctx.Rep!.Prev;
        return DoJump(JumpId.MaxUntil3, _pc);
    }

    private Signal ResumeMaxUntilTail(bool succeeded)
    {
        Repeat = _ctx.Rep;
        if (succeeded)
        {
            return Signal.Success;
        }

        Ptr = _ptr;
        return Signal.Failure;
    }

    private Signal OpMinUntil()
    {
        var repeat = EnterUntil();
        if (_ctx.Count < _code[repeat.Pattern + 1])
        {
            repeat.Count = _ctx.Count;
            return DoJump(JumpId.MinUntil1, repeat.Pattern + 3);
        }

        Repeat = repeat.Prev;
        LastMarkSave();
        if (Repeat is not null)
        {
            MarkPush(_ctx.LastMark);
        }

        return DoJump(JumpId.MinUntil2, _pc);
    }

    private Signal ResumeMinUntilTail(bool succeeded)
    {
        var repeat = _ctx.Rep!;
        var repeatOfTail = Repeat;
        Repeat = repeat;
        if (succeeded)
        {
            if (repeatOfTail is not null)
            {
                MarkPopDiscard(_ctx.LastMark);
            }

            return Signal.Success;
        }

        if (repeatOfTail is not null)
        {
            MarkPop(_ctx.LastMark);
        }

        LastMarkRestore();
        Ptr = _ptr;
        var max = _code[repeat.Pattern + 2];
        if ((_ctx.Count >= max && max != MaxRepeat) || Ptr == repeat.LastPtr)
        {
            return Signal.Failure;
        }

        repeat.Count = _ctx.Count;
        LastPtrPush(repeat);
        repeat.LastPtr = Ptr;
        return DoJump(JumpId.MinUntil3, repeat.Pattern + 3);
    }

    private Signal ResumeMinUntilItem(bool succeeded)
    {
        var repeat = _ctx.Rep!;
        LastPtrPop(repeat);
        if (succeeded)
        {
            return Signal.Success;
        }

        repeat.Count = _ctx.Count - 1;
        Ptr = _ptr;
        return Signal.Failure;
    }

    // <POSSESSIVE_REPEAT> <skip> <1=min> <2=max> pattern <SUCCESS> tail
    private Signal OpPossessiveRepeat()
    {
        Ptr = _ptr;
        var repeat = new RepeatContext { Count = -1, Pattern = NoPosition, Prev = Repeat, LastPtr = NoPosition };
        _ctx.Rep = repeat;
        Repeat = repeat;
        _ctx.Count = 0;
        return PossessiveMinimum();
    }

    private Signal PossessiveMinimum()
    {
        if (_ctx.Count < _code[_pc + 1])
        {
            return DoJump(JumpId.PossessiveRepeat1, _pc + 3, toplevel: false);
        }

        _ptr = NoPosition;
        return PossessiveMore();
    }

    private Signal ResumePossessiveMinimum(bool succeeded)
    {
        if (succeeded)
        {
            _ctx.Count++;
            return PossessiveMinimum();
        }

        Ptr = _ptr;
        Repeat = _ctx.Rep!.Prev;
        return Signal.Failure;
    }

    private Signal PossessiveMore()
    {
        var max = _code[_pc + 2];
        if ((_ctx.Count < max || max == MaxRepeat) && Ptr != _ptr)
        {
            LastMarkSave();
            MarkPush(_ctx.LastMark);
            _ptr = Ptr;
            return DoJump(JumpId.PossessiveRepeat2, _pc + 3, toplevel: false);
        }

        return PossessiveDone();
    }

    private Signal ResumePossessiveMore(bool succeeded)
    {
        if (succeeded)
        {
            MarkPopDiscard(_ctx.LastMark);
            _ctx.Count++;
            return PossessiveMore();
        }

        MarkPop(_ctx.LastMark);
        LastMarkRestore();
        Ptr = _ptr;
        return PossessiveDone();
    }

    private Signal PossessiveDone()
    {
        Repeat = _ctx.Rep!.Prev;
        _pc += (int)_code[_pc] + 1;
        _ptr = Ptr;
        return Signal.Dispatch;
    }
}
