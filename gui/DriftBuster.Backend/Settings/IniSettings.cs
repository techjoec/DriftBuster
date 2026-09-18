namespace DriftBuster.Backend.Settings;

/// <summary>
/// INI-shaped settings: <c>[section]</c> headers (registry exports included) and <c>key=value</c>, <c>key: value</c> or
/// <c>export KEY=value</c> lines. Names are <c>[section] key</c>, or the bare key before any section (.env, .properties).
/// Comment lines start with <c>#</c>, <c>;</c> or <c>//</c>. In .properties files a line ending in a backslash continues on
/// the next line; elsewhere a trailing backslash is part of the value (<c>Logs=C:\Logs\</c>).
/// </summary>
internal static class IniSettings
{
    public static ExtractedSettings Extract(string text, bool continuations = false)
    {
        var builder = new SettingsBuilder();
        var section = string.Empty;
        foreach (var line in LogicalLines(text, continuations))
        {
            if (builder.IsFull)
            {
                break;
            }

            if (line.Length == 0 || line[0] is '#' or ';' || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '[' && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var (key, value) = SplitPair(line.StartsWith("export ", StringComparison.Ordinal) ? line["export ".Length..].TrimStart() : line);
            builder.Add(section.Length == 0 ? key : $"[{section}] {key}", value);
        }

        return builder.Build(SettingsMode.Parsed);
    }

    // Trimmed lines; with continuations, a trailing backslash joins a line to the next.
    private static IEnumerable<string> LogicalLines(string text, bool continuations)
    {
        var pending = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (continuations && line.EndsWith('\\'))
            {
                pending += line[..^1];
                continue;
            }

            yield return pending + line;
            pending = string.Empty;
        }

        if (pending.Length > 0)
        {
            yield return pending;
        }
    }

    private static (string Key, string Value) SplitPair(string line)
    {
        var separator = line.IndexOfAny(['=', ':']);
        return separator > 0
            ? (Unquote(line[..separator].Trim()), Unquote(line[(separator + 1)..].Trim()))
            : (Unquote(line), string.Empty);
    }

    internal static string Unquote(string text) =>
        text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\'') ? text[1..^1] : text;
}
