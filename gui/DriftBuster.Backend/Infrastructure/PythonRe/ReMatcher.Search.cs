namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary><c>pattern_match</c>, <c>SRE(search)</c> and the scanner behind <c>finditer</c>.</summary>
internal sealed partial class ReMatcher
{
    /// <summary><c>pattern.match(text)</c> after <see cref="Reset"/>: a match starting at <see cref="Start"/>.</summary>
    public bool MatchHere()
    {
        BeginRun();
        if (_program.MinWidth > 0 && End - Ptr < _program.MinWidth)
        {
            return false;
        }

        return Run(0, toplevel: true);
    }

    /// <summary>
    /// <c>SRE(search)</c> from <see cref="Start"/>: on success <see cref="Start"/> and <see cref="Ptr"/> hold the match span.
    /// Start positions whose code point is outside <see cref="ReProgram.FirstSet"/> are passed over, as the <c>INFO</c>
    /// literal prefix and charset let <c>_sre</c> pass them over.
    /// </summary>
    public bool Search()
    {
        BeginRun();
        var ptr = Start;
        var end = End;
        if (ptr > end)
        {
            return false;
        }

        var min = _program.MinWidth;
        if (min > 0 && end - ptr < min)
        {
            return false;
        }

        if (min > 1)
        {
            end = (int)Math.Max(ptr, end - (min - 1));
        }

        var filter = _program.FirstSet;
        var first = true;
        while (true)
        {

            var matched = false;
            if (filter is null || (ptr < End && filter.Contains(Text[ptr])))
            {
                Start = ptr;
                Ptr = ptr;
                matched = Run(0, toplevel: first);
            }

            if (first)
            {
                MustAdvance = false;
                first = false;
                if (!matched && StartsAtBeginning())
                {
                    return false;
                }
            }

            if (matched)
            {
                return true;
            }

            if (ptr >= end)
            {
                return false;
            }

            ptr++;
            LastMark = -1;
            LastIndex = -1;
        }
    }

    private bool StartsAtBeginning()
        => (ReOpcode)_code[0] == ReOpcode.At && (ReAtCode)_code[1] is ReAtCode.Beginning or ReAtCode.BeginningString;

    /// <summary>The scanner step of <c>finditer</c>: search from <paramref name="start"/>, refusing an empty match there when
    /// <paramref name="mustAdvance"/>.</summary>
    public bool ScannerSearch(int start, bool mustAdvance)
    {
        StateReset();
        Start = start;
        Ptr = start;
        MustAdvance = mustAdvance;
        return Search();
    }

    /// <summary>The span of group <paramref name="group"/> (1-based) as code point offsets, or (-1, -1) when it did not take part.</summary>
    public (int Start, int End) GroupSpan(int group)
    {
        var j = (group - 1) * 2;
        if (j + 1 <= LastMark && Marks[j] != NoPosition && Marks[j + 1] != NoPosition)
        {
            if (Marks[j] > Marks[j + 1])
            {
                throw new InvalidOperationException("The span of capturing group is wrong, please report a bug for the re module.");
            }

            return (Marks[j], Marks[j + 1]);
        }

        return (NoPosition, NoPosition);
    }
}
