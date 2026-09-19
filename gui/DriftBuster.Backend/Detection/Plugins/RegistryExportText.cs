using System.Globalization;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Reads Windows Registry Editor export files (<c>.reg</c>): the <c>Windows Registry Editor Version 5.00</c> or <c>REGEDIT4</c>
/// header, <c>[key]</c> and <c>[-key]</c> sections, <c>"name"=data</c> and <c>@=data</c> value lines, <c>;</c> comments, and
/// <c>hex</c> data wrapped over lines that end in a backslash.
/// </summary>
/// <remarks>Derived from publicly documented behavior, not vendor source (Microsoft's documentation of the .reg file syntax).</remarks>
internal static class RegistryExportText
{
    internal const string Version5Header = "Windows Registry Editor Version 5.00";
    internal const string Version4Header = "REGEDIT4";

    /// <summary>The name a key's unnamed value (<c>@</c>) is shown with, as regedit shows it.</summary>
    internal const string DefaultValueName = "(Default)";

    /// <summary>One section or value line of an export, in file order.</summary>
    internal readonly record struct Entry(string KeyPath, bool KeyDeleted, string? ValueName, string? RawData);

    /// <summary>
    /// The sample's text: the decoded text when the detector had one, otherwise the sample decoded by its UTF-16 byte order
    /// mark (regedit writes version 5 exports as UTF-16LE with a mark, which the detector's text check rejects). Null when
    /// there is neither.
    /// </summary>
    internal static string? SampleText(byte[] sample, string? text)
    {
        if (text is not null)
        {
            return text;
        }

        return sample is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] ? TextDecoding.Decode(sample, Encoding.Latin1) : null;
    }

    /// <summary><c>5.00</c> or <c>4</c> when the first non-blank line is an export header, otherwise null.</summary>
    internal static string? HeaderVersion(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            return line switch
            {
                Version5Header => "5.00",
                Version4Header => "4",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>The sections and values after the header. A value line before any section is ignored, as regedit ignores it.</summary>
    internal static IEnumerable<Entry> Entries(string text)
    {
        string? key = null;
        var headerSeen = false;
        foreach (var line in LogicalLines(text))
        {
            if (!headerSeen)
            {
                headerSeen = line.Length > 0;
                continue;
            }

            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                var inner = line[1..^1].Trim();
                var deleted = inner.StartsWith('-');
                key = deleted ? inner[1..].Trim() : inner;
                yield return new Entry(key, deleted, null, null);
                continue;
            }

            if (key is null || !TrySplitValue(line, out var name, out var data))
            {
                continue;
            }

            yield return new Entry(key, false, name, data);
        }
    }

    /// <summary>
    /// A value's data as a reader would want to see it: a string unescaped; <c>hex(2)</c> (expandable string) and <c>hex(7)</c>
    /// (multi-string) decoded, UTF-16LE in version 5 exports and Latin-1 in <c>REGEDIT4</c>, multi-string items one per line;
    /// <c>-</c> as <c>(deleted)</c>; <c>dword</c> as written plus its decimal value; every other hex type as written, with the line
    /// wrapping removed.
    /// </summary>
    internal static string RenderData(string raw, bool unicode)
    {
        if (string.Equals(raw, "-", StringComparison.Ordinal))
        {
            return "(deleted)";
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return Unescape(raw[1..^1]);
        }

        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return raw;
        }

        var type = raw[..colon].Trim().ToLowerInvariant();
        var body = raw[(colon + 1)..];
        if (type is "hex(2)" or "hex(7)" && TryParseBytes(body, out var bytes))
        {
            var decoded = (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(bytes);
            var items = decoded.Split('\0');
            return string.Equals(type, "hex(2)", StringComparison.Ordinal)
                ? items[0]
                : string.Join('\n', items.Where(item => item.Length > 0));
        }

        var compact = string.Concat(body.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
        if (string.Equals(type, "dword", StringComparison.Ordinal)
            && uint.TryParse(compact, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
        {
            return string.Create(CultureInfo.InvariantCulture, $"dword:{compact} ({number})");
        }

        return type + ":" + compact;
    }

    /// <summary>
    /// Whether a value's data is a setting: a string, a deletion, <c>dword</c>, or <c>hex(2)</c>/<c>hex(7)</c> (strings) and
    /// <c>hex(4)</c>/<c>hex(5)</c>/<c>hex(b)</c> (numbers). <c>hex:</c> (<c>REG_BINARY</c>) and every other <c>hex(n)</c> type is
    /// binary data.
    /// </summary>
    internal static bool IsSettingData(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (raw.StartsWith('"') || colon < 0)
        {
            return true;
        }

        return raw[..colon].Trim().ToLowerInvariant() is "dword" or "hex(2)" or "hex(7)" or "hex(4)" or "hex(5)" or "hex(b)";
    }

    // Trimmed lines with universal newlines; a value line that ends in a backslash (hex data) joins the next line.
    private static IEnumerable<string> LogicalLines(string text)
    {
        var pending = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (pending.Length == 0 && line.Length > 0 && line[0] == '﻿')
            {
                line = line.TrimStart('﻿').Trim();
            }

            // Only a value's first line decides whether it wraps; the lines after it continue the same hex data.
            if (line.EndsWith('\\') && (pending.Length > 0 || IsWrappedHex(line)))
            {
                pending.Append(line.AsSpan(0, line.Length - 1));
                continue;
            }

            if (pending.Length > 0)
            {
                pending.Append(line);
                yield return pending.ToString();
                pending.Clear();
                continue;
            }

            yield return line;
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }

    // A wrapped line holds hex data: its value part, after the closing quote of the name, starts with "hex".
    private static bool IsWrappedHex(string line) =>
        TrySplitValue(line, out _, out var data) && data.StartsWith("hex", StringComparison.OrdinalIgnoreCase);

    // "name"=data or @=data; the quoted name may hold \" and \\ escapes.
    private static bool TrySplitValue(string line, out string name, out string data)
    {
        name = string.Empty;
        data = string.Empty;
        int end;
        if (line.StartsWith('@'))
        {
            name = DefaultValueName;
            end = 1;
        }
        else if (line.StartsWith('"'))
        {
            var close = ClosingQuote(line, 1);
            if (close < 0)
            {
                return false;
            }

            name = Unescape(line[1..close]);
            end = close + 1;
        }
        else
        {
            return false;
        }

        var rest = line.AsSpan(end).TrimStart();
        if (rest.Length == 0 || rest[0] != '=')
        {
            return false;
        }

        data = rest[1..].Trim().ToString();
        return true;
    }

    private static int ClosingQuote(string line, int start)
    {
        for (var index = start; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                index++;
                continue;
            }

            if (line[index] == '"')
            {
                return index;
            }
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    private static bool TryParseBytes(string body, out byte[] bytes)
    {
        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bytes = new byte[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!byte.TryParse(parts[index], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return false;
            }
        }

        return true;
    }
}
