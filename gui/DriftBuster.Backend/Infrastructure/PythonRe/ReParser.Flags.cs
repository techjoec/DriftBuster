using System.Globalization;

namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>Conditional groups and <c>_parse_flags</c>.</summary>
internal static partial class ReParser
{
    private static void ParseConditional(ReTokenizer source, ReParseState state, ReSubPattern subpattern, bool verbose, int nested, int start)
    {
        var condName = source.GetUntil(')', "group name");
        var nameLength = ReTokenizer.CodePoints(condName).Length;
        int condGroup;
        if (!condName.All(ch => ch is >= '0' and <= '9'))
        {
            source.CheckGroupName(condName, 1);
            if (!state.GroupDict.TryGetValue(condName, out condGroup))
            {
                throw source.Error($"unknown group name {PythonRepr.StrRepr(condName)}", nameLength + 1);
            }
        }
        else
        {
            var number = System.Numerics.BigInteger.Parse(condName, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number.IsZero)
            {
                throw source.Error("bad group number", nameLength + 1);
            }

            if (number >= ReParseState.MaxGroups)
            {
                throw source.Error($"invalid group reference {number}", nameLength + 1);
            }

            condGroup = (int)number;
            state.GroupRefPositions.TryAdd(condGroup, source.Tell() - nameLength - 1);
        }

        state.CheckLookbehindGroup(condGroup, source);
        var yes = ParseSequence(source, state, verbose, nested + 1, first: false);
        ReSubPattern? no = null;
        if (source.Match('|'))
        {
            no = ParseSequence(source, state, verbose, nested + 1, first: false);
            if (ReTokenizer.Is(source.Next, '|'))
            {
                throw source.Error("conditional backref with more than two branches");
            }
        }

        if (!source.Match(')'))
        {
            throw source.Error("missing ), unterminated subpattern", source.Tell() - start);
        }

        subpattern.Nodes.Add(new ReNode.GroupRefExists(condGroup, yes, no));
    }

    /// <summary><c>_parse_flags</c>: null for global flags (<c>(?i)</c>), otherwise the scoped add and delete sets.</summary>
    private static (PythonReFlags Add, PythonReFlags Del)? ParseFlags(ReTokenizer source, ReParseState state, int code)
    {
        var add = PythonReFlags.None;
        var del = PythonReFlags.None;
        int[]? token = null;
        if (code != '-')
        {
            while (true)
            {
                var flag = FlagFor(code)!.Value;
                if (code == 'L')
                {
                    throw source.Error("bad inline flags: cannot use 'L' flag with a str pattern");
                }

                add |= flag;
                if ((flag & TypeFlags) != PythonReFlags.None && (add & TypeFlags) != flag)
                {
                    throw source.Error("bad inline flags: flags 'a', 'u' and 'L' are incompatible");
                }

                token = source.Get() ?? throw source.Error("missing -, : or )");
                if (ReTokenizer.IsIn(token, ")-:"))
                {
                    break;
                }

                code = FlagToken(token);
                if (FlagFor(code) is null)
                {
                    throw source.Error(IsAlpha(token) ? "unknown flag" : "missing -, : or )", token.Length);
                }
            }
        }

        if (token is not null && ReTokenizer.Is(token, ')'))
        {
            state.Flags |= add;
            return null;
        }

        if (code == '-' || ReTokenizer.Is(token, '-'))
        {
            del = ParseDeletedFlags(source);
        }

        if ((add & del) != PythonReFlags.None)
        {
            throw source.Error("bad inline flags: flag turned on and off", 1);
        }

        return (add, del);
    }

    private static PythonReFlags ParseDeletedFlags(ReTokenizer source)
    {
        var del = PythonReFlags.None;
        var token = source.Get() ?? throw source.Error("missing flag");
        if (FlagFor(FlagToken(token)) is null)
        {
            throw source.Error(IsAlpha(token) ? "unknown flag" : "missing flag", token.Length);
        }

        while (true)
        {
            var flag = FlagFor(token[0])!.Value;
            if ((flag & TypeFlags) != PythonReFlags.None)
            {
                throw source.Error("bad inline flags: cannot turn off flags 'a', 'u' and 'L'");
            }

            del |= flag;
            token = source.Get() ?? throw source.Error("missing :");
            if (ReTokenizer.Is(token, ':'))
            {
                return del;
            }

            if (FlagFor(FlagToken(token)) is null)
            {
                throw source.Error(IsAlpha(token) ? "unknown flag" : "missing :", token.Length);
            }
        }
    }

    private static int FlagToken(int[] token) => token.Length == 1 ? token[0] : -1;

    // str.isalpha() of a token: every code point a letter (an escape token holds a backslash, so it never is).
    private static bool IsAlpha(int[] token)
        => token.All(PythonUnicode.IsAlpha);
}
