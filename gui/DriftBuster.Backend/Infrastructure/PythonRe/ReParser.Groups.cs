namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>Parenthesised constructs and inline flags.</summary>
internal static partial class ReParser
{
    private const PythonReFlags TypeFlags = PythonReFlags.Ascii | PythonReFlags.Locale | PythonReFlags.Unicode;

    private static PythonReFlags? FlagFor(int code) => code switch
    {
        'i' => PythonReFlags.IgnoreCase,
        'L' => PythonReFlags.Locale,
        'm' => PythonReFlags.Multiline,
        's' => PythonReFlags.DotAll,
        'x' => PythonReFlags.Verbose,
        'a' => PythonReFlags.Ascii,
        'u' => PythonReFlags.Unicode,
        _ => null,
    };

    private static bool ParseParen(ReTokenizer source, ReParseState state, ReSubPattern subpattern, bool verbose, int nested, bool first)
    {
        var start = source.Tell() - 1;
        if (!source.Match('?'))
        {
            AddGroup(source, state, subpattern, new GroupSpec(Capture: true, Atomic: false, Name: null), verbose, nested, start);
            return verbose;
        }

        var token = source.Get() ?? throw source.Error("unexpected end of pattern");
        var code = token.Length == 1 ? token[0] : -1;
        switch (code)
        {
            case 'P':
                return ParsePythonExtension(source, state, subpattern, verbose, nested, start);
            case ':':
                AddGroup(source, state, subpattern, new GroupSpec(Capture: false, Atomic: false, Name: null), verbose, nested, start);
                return verbose;
            case '#':
                SkipComment(source, start);
                return verbose;
            case '=' or '!' or '<':
                ParseAssertion(source, state, subpattern, code, verbose, nested, start);
                return verbose;
            case '(':
                ParseConditional(source, state, subpattern, verbose, nested, start);
                return verbose;
            case '>':
                AddGroup(source, state, subpattern, new GroupSpec(Capture: false, Atomic: true, Name: null), verbose, nested, start);
                return verbose;
        }

        if (code == '-' || FlagFor(code) is not null)
        {
            var flags = ParseFlags(source, state, code);
            if (flags is null)
            {
                if (!first || subpattern.Nodes.Count > 0)
                {
                    throw source.Error("global flags not at the start of the expression", source.Tell() - start);
                }

                return state.Flags.HasFlag(PythonReFlags.Verbose);
            }

            var spec = new GroupSpec(Capture: false, Atomic: false, Name: null, flags.Value.Add, flags.Value.Del);
            AddGroup(source, state, subpattern, spec, verbose, nested, start);
            return verbose;
        }

        throw source.Error("unknown extension ?" + TokenText(token), token.Length + 1);
    }

    private static bool ParsePythonExtension(ReTokenizer source, ReParseState state, ReSubPattern subpattern, bool verbose, int nested, int start)
    {
        if (source.Match('<'))
        {
            var name = source.GetUntil('>', "group name");
            source.CheckGroupName(name, 1);
            AddGroup(source, state, subpattern, new GroupSpec(Capture: true, Atomic: false, Name: name), verbose, nested, start);
            return verbose;
        }

        if (source.Match('='))
        {
            var name = source.GetUntil(')', "group name");
            source.CheckGroupName(name, 1);
            var nameLength = ReTokenizer.CodePoints(name).Length;
            if (!state.GroupDict.TryGetValue(name, out var gid))
            {
                throw source.Error($"unknown group name {PythonRepr.StrRepr(name)}", nameLength + 1);
            }

            if (!state.CheckGroup(gid))
            {
                throw source.Error("cannot refer to an open group", nameLength + 1);
            }

            state.CheckLookbehindGroup(gid, source);
            subpattern.Nodes.Add(new ReNode.GroupRef(gid));
            return verbose;
        }

        var token = source.Get() ?? throw source.Error("unexpected end of pattern");
        throw source.Error("unknown extension ?P" + TokenText(token), token.Length + 2);
    }

    private static void SkipComment(ReTokenizer source, int start)
    {
        while (true)
        {
            if (source.Next is null)
            {
                throw source.Error("missing ), unterminated comment", source.Tell() - start);
            }

            if (ReTokenizer.Is(source.Get(), ')'))
            {
                return;
            }
        }
    }

    private static void ParseAssertion(ReTokenizer source, ReParseState state, ReSubPattern subpattern, int code, bool verbose, int nested, int start)
    {
        var behind = false;
        int? savedLookbehind = null;
        if (code == '<')
        {
            var token = source.Get() ?? throw source.Error("unexpected end of pattern");
            if (!ReTokenizer.IsIn(token, "=!"))
            {
                throw source.Error("unknown extension ?<" + TokenText(token), token.Length + 2);
            }

            code = token[0];
            behind = true;
            savedLookbehind = state.LookbehindGroups;
            state.LookbehindGroups ??= state.Groups;
        }

        var body = ParseSub(source, state, verbose, nested + 1);
        if (behind && savedLookbehind is null)
        {
            state.LookbehindGroups = null;
        }

        if (!source.Match(')'))
        {
            throw source.Error("missing ), unterminated subpattern", source.Tell() - start);
        }

        if (code == '=')
        {
            subpattern.Nodes.Add(new ReNode.Assert(negative: false, behind, body));
        }
        else if (body.Nodes.Count > 0)
        {
            subpattern.Nodes.Add(new ReNode.Assert(negative: true, behind, body));
        }
        else
        {
            subpattern.Nodes.Add(new ReNode.Failure());
        }
    }

    private readonly record struct GroupSpec(bool Capture, bool Atomic, string? Name, PythonReFlags Add = PythonReFlags.None, PythonReFlags Del = PythonReFlags.None);

    private static void AddGroup(ReTokenizer source, ReParseState state, ReSubPattern subpattern, GroupSpec spec, bool verbose, int nested, int start)
    {
        int? group = null;
        if (spec.Capture)
        {
            try
            {
                group = state.OpenGroup(spec.Name);
            }
            catch (PythonReException error)
            {
                throw source.Error(error.Message, ReTokenizer.CodePoints(spec.Name ?? string.Empty).Length + 1);
            }
        }

        var subVerbose = (verbose || spec.Add.HasFlag(PythonReFlags.Verbose)) && !spec.Del.HasFlag(PythonReFlags.Verbose);
        var body = ParseSub(source, state, subVerbose, nested + 1);
        if (!source.Match(')'))
        {
            throw source.Error("missing ), unterminated subpattern", source.Tell() - start);
        }

        if (group is { } gid)
        {
            state.CloseGroup(gid, body);
        }

        subpattern.Nodes.Add(spec.Atomic ? new ReNode.Atomic(body) : new ReNode.Subpattern(group, spec.Add, spec.Del, body));
    }
}
