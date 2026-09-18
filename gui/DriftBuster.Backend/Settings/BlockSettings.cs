namespace DriftBuster.Backend.Settings;

/// <summary>
/// Settings from directive files (nginx and other unix conf, HCL, Dockerfile, plain text): each line is a setting named by its
/// leading word(s) and valued by the rest, split at <c>=</c>, <c>:</c> or the first space. A line ending in <c>{</c> opens a block
/// whose words prefix the names inside it (<c>http.server.listen</c>); <c>}</c> closes it. Comment lines start with <c>#</c>,
/// <c>//</c> or <c>;</c>, a trailing <c>;</c> is dropped, and a backslash at the end of a line continues it.
/// </summary>
internal static class BlockSettings
{
    public static ExtractedSettings Extract(string text)
    {
        var builder = new SettingsBuilder();
        var blocks = new Stack<string>();
        var pending = string.Empty;
        foreach (var rawLine in text.Split('\n'))
        {
            if (builder.IsFull)
            {
                break;
            }

            var line = rawLine.TrimEnd('\r').Trim();
            if (line.EndsWith('\\'))
            {
                pending += line[..^1].TrimEnd() + " ";
                continue;
            }

            line = (pending + line).Trim();
            pending = string.Empty;
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line is "}" or "};")
            {
                if (blocks.Count > 0)
                {
                    blocks.Pop();
                }

                continue;
            }

            if (line.EndsWith('{'))
            {
                var header = line[..^1].Trim().TrimEnd('=').Trim();
                blocks.Push(header.Length == 0 ? "{}" : string.Join('.', header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(IniSettings.Unquote)));
                continue;
            }

            if (line.EndsWith(';'))
            {
                line = line[..^1].TrimEnd();
            }

            var (key, value) = Split(line);
            var prefix = string.Join('.', blocks.Reverse());
            builder.Add(prefix.Length == 0 ? key : $"{prefix}.{key}", value);
        }

        return builder.Build(SettingsMode.Lines);
    }

    private static (string Key, string Value) Split(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        var space = line.IndexOfAny([' ', '\t']);
        if (equals > 0 && (space < 0 || line[..equals].Trim().IndexOfAny([' ', '\t']) < 0))
        {
            return (IniSettings.Unquote(line[..equals].Trim()), IniSettings.Unquote(line[(equals + 1)..].Trim()));
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && (space < 0 || colon < space) && colon + 1 < line.Length && line[colon + 1] == ' ')
        {
            return (line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return space > 0 ? (line[..space], line[(space + 1)..].Trim()) : (line, string.Empty);
    }
}
