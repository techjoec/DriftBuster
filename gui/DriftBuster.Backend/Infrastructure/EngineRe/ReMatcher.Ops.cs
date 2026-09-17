namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>The non-repeat opcodes of <c>SRE(match)</c>.</summary>
internal sealed partial class ReMatcher
{
    private Signal OpMark()
    {
        var i = (int)_code[_pc];
        if ((i & 1) != 0)
        {
            LastIndex = (i / 2) + 1;
        }

        if (i > LastMark)
        {
            for (var j = LastMark + 1; j < i; j++)
            {
                Marks[j] = NoPosition;
            }

            LastMark = i;
        }

        Marks[i] = _ptr;
        _pc++;
        return Signal.Dispatch;
    }

    private Signal OpChar()
    {
        if (_ptr >= End || !_program.Sets[_code[_pc]].Contains(Text[_ptr]))
        {
            return Signal.Failure;
        }

        _pc++;
        _ptr++;
        return Signal.Dispatch;
    }

    private Signal OpSuccess()
    {
        if (_ctx.TopLevel && ((MatchAll && _ptr != End) || (MustAdvance && _ptr == Start)))
        {
            return Signal.Failure;
        }

        Ptr = _ptr;
        return Signal.Success;
    }

    private Signal OpAt()
    {
        if (!At(_ptr, (ReAtCode)_code[_pc]))
        {
            return Signal.Failure;
        }

        _pc++;
        return Signal.Dispatch;
    }

    // SRE(at).
    private bool At(int ptr, ReAtCode at)
    {
        switch (at)
        {
            case ReAtCode.Beginning or ReAtCode.BeginningString:
                return ptr == 0;
            case ReAtCode.BeginningLine:
                return ptr == 0 || Text[ptr - 1] == '\n';
            case ReAtCode.End:
                return (End - ptr == 1 && Text[ptr] == '\n') || ptr == End;
            case ReAtCode.EndLine:
                return ptr == End || Text[ptr] == '\n';
            case ReAtCode.EndString:
                return ptr == End;
        }

        if (End == 0)
        {
            return false;
        }

        var unicode = at is ReAtCode.UniBoundary or ReAtCode.UniNonBoundary;
        var before = ptr > 0 && IsWord(Text[ptr - 1], unicode);
        var after = ptr < End && IsWord(Text[ptr], unicode);
        return at is ReAtCode.Boundary or ReAtCode.UniBoundary ? before != after : before == after;
    }

    private static bool IsWord(int code, bool unicode)
        => unicode ? EngineCharacterData.Word.Contains(code) : code < 128 && EngineCharacterData.AsciiWord.Contains(code);

    private Signal OpJump()
    {
        _pc += (int)_code[_pc];
        return Signal.Dispatch;
    }

    private Signal OpBranch()
    {
        LastMarkSave();
        if (Repeat is not null)
        {
            MarkPush(_ctx.LastMark);
        }

        return BranchNext();
    }

    private Signal BranchNext()
    {
        for (; _code[_pc] != 0; _pc += (int)_code[_pc])
        {
            // The LITERAL / IN look-ahead of SRE_OP_BRANCH: an alternative whose first opcode cannot match here is skipped.
            if ((ReOpcode)_code[_pc + 1] == ReOpcode.Char && (_ptr >= End || !_program.Sets[_code[_pc + 2]].Contains(Text[_ptr])))
            {
                continue;
            }

            Ptr = _ptr;
            return DoJump(JumpId.Branch, _pc + 1);
        }

        if (Repeat is not null)
        {
            MarkPopDiscard(_ctx.LastMark);
        }

        return Signal.Failure;
    }

    private Signal ResumeBranch(bool succeeded)
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
        _pc += (int)_code[_pc];
        return BranchNext();
    }

    private Signal OpGroupRef(ReOpcode op)
    {
        var groupRef = (int)_code[_pc] * 2;
        if (groupRef >= LastMark)
        {
            return Signal.Failure;
        }

        var p = Marks[groupRef];
        var e = Marks[groupRef + 1];
        if (p == NoPosition || e == NoPosition || e < p)
        {
            return Signal.Failure;
        }

        for (; p < e; p++, _ptr++)
        {
            if (_ptr >= End || !SameCharacter(op, Text[_ptr], Text[p]))
            {
                return Signal.Failure;
            }
        }

        _pc++;
        return Signal.Dispatch;
    }

    private static bool SameCharacter(ReOpcode op, int left, int right) => op switch
    {
        ReOpcode.GroupRefIgnore => EngineCharacterData.AsciiLower(left) == EngineCharacterData.AsciiLower(right),
        ReOpcode.GroupRefUniIgnore => EngineCharacterData.Lower(left) == EngineCharacterData.Lower(right),
        _ => left == right,
    };

    private Signal OpGroupRefExists()
    {
        var groupRef = (int)_code[_pc] * 2;
        if (groupRef >= LastMark || Marks[groupRef] == NoPosition || Marks[groupRef + 1] == NoPosition || Marks[groupRef + 1] < Marks[groupRef])
        {
            _pc += (int)_code[_pc + 1];
            return Signal.Dispatch;
        }

        _pc += 2;
        return Signal.Dispatch;
    }

    private Signal OpAssert()
    {
        var back = _code[_pc + 1];
        if (_ptr < back)
        {
            return Signal.Failure;
        }

        Ptr = _ptr - (int)back;
        return DoJump(JumpId.Assert, _pc + 2, toplevel: false);
    }

    private Signal ResumeAssert(bool succeeded)
    {
        if (!succeeded)
        {
            return Signal.Failure;
        }

        _pc += (int)_code[_pc];
        return Signal.Dispatch;
    }

    private Signal OpAssertNot()
    {
        var back = _code[_pc + 1];
        if (_ptr >= back)
        {
            Ptr = _ptr - (int)back;
            LastMarkSave();
            if (Repeat is not null)
            {
                MarkPush(_ctx.LastMark);
            }

            return DoJump(JumpId.AssertNot, _pc + 2, toplevel: false);
        }

        _pc += (int)_code[_pc];
        return Signal.Dispatch;
    }

    private Signal ResumeAssertNot(bool succeeded)
    {
        if (succeeded)
        {
            if (Repeat is not null)
            {
                MarkPopDiscard(_ctx.LastMark);
            }

            return Signal.Failure;
        }

        if (Repeat is not null)
        {
            MarkPop(_ctx.LastMark);
        }

        LastMarkRestore();
        _pc += (int)_code[_pc];
        return Signal.Dispatch;
    }

    private Signal OpAtomicGroup()
    {
        Ptr = _ptr;
        return DoJump(JumpId.AtomicGroup, _pc + 1, toplevel: false);
    }

    private Signal ResumeAtomicGroup(bool succeeded)
    {
        if (!succeeded)
        {
            Ptr = _ptr;
            return Signal.Failure;
        }

        _pc += (int)_code[_pc];
        _ptr = Ptr;
        return Signal.Dispatch;
    }
}
