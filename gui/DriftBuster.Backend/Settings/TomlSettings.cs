using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// TOML settings from a small reader written from the TOML specification: <c>[table]</c> and <c>[[array.of.tables]]</c> headers
/// (the latter numbered <c>name[1]</c>, <c>name[2]</c>), bare, quoted and dotted keys, and <c>key = value</c> pairs. Basic and literal
/// strings are unquoted; arrays, inline tables and other values keep their written text, gathered across lines until brackets
/// and triple-quoted strings close. Returns null when a line is not TOML, so the caller falls back to lines.
/// </summary>
internal static class TomlSettings
{
    public static ExtractedSettings? Extract(string text)
    {
        var builder = new SettingsBuilder();
        var table = string.Empty;
        var arrayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length && !builder.IsFull; index++)
        {
            var line = StripComment(lines[index].TrimEnd('\r')).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                var name = JoinKey(line[2..^2]);
                var count = arrayCounts.TryGetValue(name, out var seen) ? seen + 1 : 1;
                arrayCounts[name] = count;
                table = string.Create(CultureInfo.InvariantCulture, $"{name}[{count}]");
                continue;
            }

            if (line[0] == '[' && line.EndsWith(']'))
            {
                table = JoinKey(line[1..^1]);
                continue;
            }

            var equals = FindEquals(line);
            if (equals <= 0)
            {
                return null;
            }

            var key = JoinKey(line[..equals]);
            var value = new StringBuilder(line[(equals + 1)..].Trim());
            while (!IsComplete(value.ToString()) && index + 1 < lines.Length)
            {
                index++;
                value.Append('\n').Append(StripComment(lines[index].TrimEnd('\r')).Trim());
            }

            builder.Add(table.Length == 0 ? key : $"{table}.{key}", Unquote(value.ToString()));
        }

        return builder.Build(SettingsMode.Parsed);
    }

    // The first '=' outside quotes.
    private static int FindEquals(string line)
    {
        char? quote = null;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '=')
            {
                return index;
            }
        }

        return -1;
    }

    // "a . b" / "'a.b'" -> a.b / a.b, quotes removed from each part.
    private static string JoinKey(string raw)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in raw.Trim())
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '.')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        parts.Add(current.ToString().Trim());
        return string.Join('.', parts);
    }

    // A '#' outside quotes starts a comment.
    private static string StripComment(string line)
    {
        char? quote = null;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (quote is not null)
            {
                if (ch == quote && (quote == '\'' || index == 0 || line[index - 1] != '\\'))
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    // Brackets and braces balanced, and triple-quoted strings closed.
    private static bool IsComplete(string value)
    {
        foreach (var fence in (string[])["\"\"\"", "'''"])
        {
            if (value.StartsWith(fence, StringComparison.Ordinal))
            {
                return value.Length >= 6 && value.EndsWith(fence, StringComparison.Ordinal);
            }
        }

        var depth = 0;
        char? quote = null;
        foreach (var ch in value)
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch is '[' or '{')
            {
                depth++;
            }
            else if (ch is ']' or '}')
            {
                depth--;
            }
        }

        return depth <= 0;
    }

    private static string Unquote(string value)
    {
        foreach (var fence in (string[])["\"\"\"", "'''"])
        {
            if (value.Length >= 6 && value.StartsWith(fence, StringComparison.Ordinal) && value.EndsWith(fence, StringComparison.Ordinal))
            {
                return value[3..^3].TrimStart('\n');
            }
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return IniSettings.Unquote(value);
    }
}
