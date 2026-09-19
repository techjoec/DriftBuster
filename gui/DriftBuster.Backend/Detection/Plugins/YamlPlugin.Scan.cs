using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Hand-written matchers for the YAML patterns. An anchor is a multiline <c>^</c> position: offset 0 or just after a <c>\n</c>
/// (never after <c>\r</c>). Whitespace runs use <see cref="char.IsWhiteSpace(char)"/> and may cross line breaks, as the patterns allow.
/// </summary>
public sealed partial class YamlPlugin
{
    private static int SkipSpace(string s, int offset)
    {
        while (offset < s.Length && char.IsWhiteSpace(s[offset]))
        {
            offset++;
        }

        return offset;
    }

    // The first anchor at or after "from"; s.Length when none is left.
    private static int NextAnchor(string s, int from)
    {
        if (from <= 0)
        {
            return 0;
        }

        if (from >= s.Length)
        {
            return s.Length;
        }

        if (s[from - 1] == '\n')
        {
            return from;
        }

        var index = s.IndexOf('\n', from);
        return index < 0 ? s.Length : index + 1;
    }

    private static int RuneLength(string s, int offset)
        => char.IsHighSurrogate(s[offset]) && offset + 1 < s.Length && char.IsLowSurrogate(s[offset + 1]) ? 2 : 1;

    // [A-Za-z_][\w.-]* on code points; the token is greedy and, since [\w.-], \s and ':' are disjoint, never backtracks.
    private static bool TryKeyToken(string s, int offset, out int end)
    {
        end = offset;
        if (offset >= s.Length || !(char.IsAsciiLetter(s[offset]) || s[offset] == '_'))
        {
            return false;
        }

        end = offset + 1;
        while (end < s.Length)
        {
            Rune.DecodeFromUtf16(s.AsSpan(end), out var rune, out var consumed);
            if (rune.Value is '.' or '-' || rune.IsWordCharacter)
            {
                end += consumed;
                continue;
            }

            break;
        }

        return true;
    }

    /// <summary>
    /// Key tokens of the non-overlapping matches of <c>^\s*[A-Za-z_][\w.-]*\s*:\s*(\S|$)</c> (multiline). The tail swallows the
    /// first non-space character after the colon, even on a later line, so a key on that line is not counted.
    /// </summary>
    private static List<string> KeyColonMatches(string s)
    {
        var keys = new List<string>();
        var anchor = 0;
        while (anchor < s.Length)
        {
            var keyStart = SkipSpace(s, anchor);
            if (TryKeyToken(s, keyStart, out var keyEnd))
            {
                var colon = SkipSpace(s, keyEnd);
                if (colon < s.Length && s[colon] == ':')
                {
                    keys.Add(s[keyStart..keyEnd]);
                    var value = SkipSpace(s, colon + 1);
                    anchor = NextAnchor(s, value < s.Length ? value + RuneLength(s, value) : s.Length);
                    continue;
                }
            }

            // Every anchor inside the leading whitespace run reaches the same failing position.
            anchor = NextAnchor(s, keyStart + 1);
        }

        return keys;
    }

    // ^\s*<marker>\s*$ (multiline).
    private static bool HasMarkerLine(string s, string marker)
    {
        var anchor = 0;
        while (anchor < s.Length)
        {
            var q = SkipSpace(s, anchor);
            if (s.AsSpan(q).StartsWith(marker, StringComparison.Ordinal))
            {
                var r = q + marker.Length;
                while (r < s.Length && char.IsWhiteSpace(s[r]))
                {
                    if (s[r] == '\n')
                    {
                        return true;
                    }

                    r++;
                }

                if (r == s.Length)
                {
                    return true;
                }
            }

            anchor = NextAnchor(s, q + 1);
        }

        return false;
    }

    // ^\s*-\s+\S+ (multiline); "\s+" may cross line breaks, so "-" alone on a line followed by text matches.
    private static bool HasListMarker(string s)
    {
        var anchor = 0;
        while (anchor < s.Length)
        {
            var q = SkipSpace(s, anchor);
            if (q < s.Length && s[q] == '-')
            {
                var r = SkipSpace(s, q + 1);
                if (r > q + 1 && r < s.Length)
                {
                    return true;
                }
            }

            anchor = NextAnchor(s, q + 1);
        }

        return false;
    }

    // \n\s{2,}[A-Za-z_][\w.-]*\s*:\s*(\S|$): at least two whitespace characters (line breaks included) after a "\n", then a key.
    private static bool HasIndentedBlock(string s)
    {
        var index = s.IndexOf('\n', StringComparison.Ordinal);
        while (index >= 0)
        {
            var q = SkipSpace(s, index + 1);
            if (q - (index + 1) >= 2 && TryKeyToken(s, q, out var keyEnd))
            {
                var colon = SkipSpace(s, keyEnd);
                if (colon < s.Length && s[colon] == ':')
                {
                    return true;
                }
            }

            index = s.IndexOf('\n', q);
        }

        return false;
    }

    // Non-overlapping ^\s*#\s*[A-Za-z_][\w.-]*\s*:\s* matches; the trailing "\s*" swallows following line breaks, so a commented
    // key right after another is not counted.
    private static int CountCommentedKeys(string s)
    {
        var count = 0;
        var anchor = 0;
        while (anchor < s.Length)
        {
            var q = SkipSpace(s, anchor);
            if (q < s.Length && s[q] == '#' && TryKeyToken(s, SkipSpace(s, q + 1), out var keyEnd))
            {
                var colon = SkipSpace(s, keyEnd);
                if (colon < s.Length && s[colon] == ':')
                {
                    count++;
                    anchor = NextAnchor(s, SkipSpace(s, colon + 1));
                    continue;
                }
            }

            anchor = NextAnchor(s, q + 1);
        }

        return count;
    }

    // ^\s*<literal>\s*:\s*\S+ (multiline), case-sensitive.
    private static bool HasKeyWithValue(string s, string literal)
    {
        var anchor = 0;
        while (anchor < s.Length)
        {
            var q = SkipSpace(s, anchor);
            if (s.AsSpan(q).StartsWith(literal, StringComparison.Ordinal))
            {
                var colon = SkipSpace(s, q + literal.Length);
                if (colon < s.Length && s[colon] == ':' && SkipSpace(s, colon + 1) < s.Length)
                {
                    return true;
                }
            }

            anchor = NextAnchor(s, q + 1);
        }

        return false;
    }
}
