namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>
/// A port of CPython 3.13's <c>re._parser</c> (PSF License) producing the same syntax tree and raising the same
/// <c>re.error</c> messages at the same positions. The branch common-prefix and branch-to-set rewrites, and the
/// inlining of bare non-capturing groups, are left out: they never change what a pattern matches.
/// </summary>
internal static partial class ReParser
{
    private const string SpecialChars = ".\\[{()*+?^$|";
    private const string Digits = "0123456789";
    private const string OctDigits = "01234567";
    private const string HexDigits = "0123456789abcdefABCDEF";
    private const string Whitespace = " \t\n\r\v\f";

    /// <summary><c>re._parser.parse(str, flags)</c> followed by <c>fix_flags</c>.</summary>
    public static ReSubPattern Parse(string pattern, PythonReFlags flags)
    {
        var source = new ReTokenizer(pattern);
        var state = new ReParseState { Flags = flags };
        var parsed = ParseSub(source, state, flags.HasFlag(PythonReFlags.Verbose), 0);
        state.Flags = FixFlags(state.Flags);
        if (source.Next is not null)
        {
            throw source.Error("unbalanced parenthesis");
        }

        foreach (var (group, position) in state.GroupRefPositions)
        {
            if (group >= state.Groups)
            {
                throw new PythonReException($"invalid group reference {group}", position);
            }
        }

        return parsed;
    }

    private static PythonReFlags FixFlags(PythonReFlags flags)
    {
        if (flags.HasFlag(PythonReFlags.Locale))
        {
            throw new PythonValueException("cannot use LOCALE flag with a str pattern", nameof(flags));
        }

        if (!flags.HasFlag(PythonReFlags.Ascii))
        {
            return flags | PythonReFlags.Unicode;
        }

        if (flags.HasFlag(PythonReFlags.Unicode))
        {
            throw new PythonValueException("ASCII and UNICODE flags are incompatible", nameof(flags));
        }

        return flags;
    }

    private static ReSubPattern ParseSub(ReTokenizer source, ReParseState state, bool verbose, int nested)
    {
        var items = new List<ReSubPattern>();
        while (true)
        {
            items.Add(ParseSequence(source, state, verbose, nested + 1, nested == 0 && items.Count == 0));
            if (!source.Match('|'))
            {
                break;
            }

            if (nested == 0)
            {
                verbose = state.Flags.HasFlag(PythonReFlags.Verbose);
            }
        }

        if (items.Count == 1)
        {
            return items[0];
        }

        var subpattern = new ReSubPattern(state);
        subpattern.Nodes.Add(new ReNode.Branch(items));
        return subpattern;
    }

    private static ReSubPattern ParseSequence(ReTokenizer source, ReParseState state, bool verbose, int nested, bool first)
    {
        var subpattern = new ReSubPattern(state);
        while (true)
        {
            var token = source.Next;
            if (token is null || ReTokenizer.IsIn(token, "|)"))
            {
                break;
            }

            source.Get();
            if (verbose && SkipVerbose(source, token))
            {
                continue;
            }

            if (token[0] == '\\')
            {
                subpattern.Nodes.Add(Escape(source, token, state));
            }
            else if (!ReTokenizer.IsIn(token, SpecialChars))
            {
                subpattern.Nodes.Add(new ReNode.Literal(token[0], negated: false));
            }
            else
            {
                verbose = ParseSpecial(source, state, subpattern, token, verbose, nested, first);
            }
        }

        return subpattern;
    }

    private static bool SkipVerbose(ReTokenizer source, int[] token)
    {
        if (ReTokenizer.IsIn(token, Whitespace))
        {
            return true;
        }

        if (!ReTokenizer.Is(token, '#'))
        {
            return false;
        }

        while (true)
        {
            var next = source.Get();
            if (next is null || ReTokenizer.Is(next, '\n'))
            {
                return true;
            }
        }
    }

    // Returns the (possibly updated) verbose flag: global inline flags at the start can turn it on.
    private static bool ParseSpecial(ReTokenizer source, ReParseState state, ReSubPattern subpattern, int[] token, bool verbose, int nested, bool first)
    {
        switch ((char)token[0])
        {
            case '[':
                subpattern.Nodes.Add(ParseSet(source));
                return verbose;
            case '*' or '+' or '?' or '{':
                ParseRepeat(source, subpattern, token);
                return verbose;
            case '.':
                subpattern.Nodes.Add(new ReNode.Any());
                return verbose;
            case '(':
                return ParseParen(source, state, subpattern, verbose, nested, first);
            case '^':
                subpattern.Nodes.Add(new ReNode.At(AtCode.Beginning));
                return verbose;
            default:
                subpattern.Nodes.Add(new ReNode.At(AtCode.End));
                return verbose;
        }
    }

    private static bool IsDigit(int code) => code is >= '0' and <= '9';

    private static bool IsAsciiLetter(int code) => code is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static string TokenText(int[] token) => ReTokenizer.Text(token);
}
