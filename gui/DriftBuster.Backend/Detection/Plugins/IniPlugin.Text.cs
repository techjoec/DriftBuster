using System.Globalization;
using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Hand-matched line tests for patterns whose .NET regex semantics differ (case folding, word boundaries, whitespace).</summary>
public sealed partial class IniPlugin
{
    private static int SkipSpaces(string s, int offset)
    {
        while (offset < s.Length && char.IsWhiteSpace(s[offset]))
        {
            offset++;
        }

        return offset;
    }

    // Case-insensitive ASCII keyword match, also accepting U+0130/U+0131 for 'i', U+017F for 's', U+212A for 'k'.
    private static bool MatchesKeywordIgnoreCase(string s, int offset, string keywordLower, out int end)
    {
        end = offset;
        foreach (var expected in keywordLower)
        {
            if (end >= s.Length)
            {
                return false;
            }

            Rune.DecodeFromUtf16(s.AsSpan(end), out var rune, out var consumed);
            if (!MatchesPatternChar(expected, rune))
            {
                return false;
            }

            end += consumed;
        }

        return true;
    }

    private static bool MatchesPatternChar(char expected, Rune actual)
    {
        if (!char.IsAsciiLetterLower(expected))
        {
            return actual.Value == expected;
        }

        if (actual.Value == expected || actual.Value == char.ToUpperInvariant(expected))
        {
            return true;
        }

        return expected switch
        {
            'i' => actual.Value is 0x0130 or 0x0131,
            's' => actual.Value == 0x017F,
            'k' => actual.Value == 0x212A,
            _ => false,
        };
    }

    // Word boundary after a letter: the next code point is not [L N _], or the text ends.
    private static bool AtWordEnd(string s, int offset)
    {
        if (offset >= s.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(s.AsSpan(offset), out var rune, out _);
        return !rune.IsWordCharacter;
    }

    // ^\s*[;#!] on a line.
    private static bool IsCommentLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        return offset < line.Length && line[offset] is ';' or '#' or '!';
    }

    // ^\s*(?:Include|LoadModule|SetEnv|Option|Alias)\s+\S+ on a line, case-insensitive.
    private static bool IsDirectiveLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        foreach (var keyword in DirectiveKeywords)
        {
            if (!MatchesKeywordIgnoreCase(line, offset, keyword, out var end))
            {
                continue;
            }

            var afterSpaces = SkipSpaces(line, end);
            if (afterSpaces > end && afterSpaces < line.Length)
            {
                return true;
            }
        }

        return false;
    }

    // ^\s*\[[^\]]*$ on a line: '[' first and no ']' after it.
    private static bool IsMalformedSectionLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        return offset < line.Length && line[offset] == '[' && !line.AsSpan(offset + 1).Contains(']');
    }

    // ^\s*[{}]+\s*$ on a line.
    private static bool IsStandaloneBraceLine(string line)
    {
        var stripped = line.Trim();
        return stripped.Length > 0 && stripped.All(ch => ch is '{' or '}');
    }

    // ^\s*(?:LoadModule|SetEnv|<VirtualHost|<Directory|ServerName)\b over the text, case-insensitive; whitespace runs skipped once.
    private static bool HasApacheHint(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var offset = SkipSpaces(text, start);
            foreach (var keyword in ApacheKeywords)
            {
                if (MatchesKeywordIgnoreCase(text, offset, keyword, out var end) && AtWordEnd(text, end))
                {
                    return true;
                }
            }

            start = LineStartMatcher.NextLineStart(text, offset + 1);
        }

        return false;
    }

    // ^\s*(?:server\s*\{|location\s+|upstream\s+) over the text, case-insensitive.
    private static bool HasNginxHint(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var offset = SkipSpaces(text, start);
            if (MatchesKeywordIgnoreCase(text, offset, "server", out var afterServer))
            {
                var brace = SkipSpaces(text, afterServer);
                if (brace < text.Length && text[brace] == '{')
                {
                    return true;
                }
            }

            foreach (var keyword in new[] { "location", "upstream" })
            {
                if (MatchesKeywordIgnoreCase(text, offset, keyword, out var end) && SkipSpaces(text, end) > end)
                {
                    return true;
                }
            }

            start = LineStartMatcher.NextLineStart(text, offset + 1);
        }

        return false;
    }

    /// <summary>
    /// Rounds the exact binary value to the nearest multiple of 10^-digits (ties to even) and reads that decimal back as the nearest
    /// double. <see cref="Math.Round(double, int)"/> rounds the scaled product instead, which differs when the scaling itself rounds
    /// onto a midpoint. NaN and infinities pass through; negatives keep their sign (-0.0004 gives -0.0).
    /// </summary>
    internal static double EngineRound(double value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        if (!double.IsFinite(value) || value == 0.0)
        {
            return value;
        }

        if (value < 0)
        {
            return -EngineRound(-value, digits);
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075;
        var scale = BigInteger.Pow(10, digits);
        var scaled = new BigInteger(mantissa) * scale;
        BigInteger quotient;
        if (exponent >= 0)
        {
            quotient = scaled << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            quotient = BigInteger.DivRem(scaled, denominator, out var remainder);
            var comparison = (remainder * 2).CompareTo(denominator);
            if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
            {
                quotient += BigInteger.One;
            }
        }

        return double.Parse(quotient.ToString(CultureInfo.InvariantCulture) + "E-" + digits.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
