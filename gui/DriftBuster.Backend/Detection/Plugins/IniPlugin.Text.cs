using System.Globalization;
using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Hand-matched line tests standing in for the Python regexes whose semantics .NET does not share.</summary>
public sealed partial class IniPlugin
{
    private static int SkipSpaces(string s, int offset)
    {
        while (offset < s.Length && PythonText.IsSpace(s[offset]))
        {
            offset++;
        }

        return offset;
    }

    // Python IGNORECASE on an ASCII-letter pattern: the text code point simple-lowercases to the letter, plus the
    // extra cases re adds (U+0130 and U+0131 for 'i', U+017F for 's', U+212A for 'k'); non-letters match literally.
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

    // Python \b after a letter: the next code point is not [L N _], or the text ends.
    private static bool AtWordEnd(string s, int offset)
    {
        if (offset >= s.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(s.AsSpan(offset), out var rune, out _);
        return !PythonText.IsWordRune(rune);
    }

    // ^\s*[;#!] via match() on a line.
    private static bool IsCommentLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        return offset < line.Length && line[offset] is ';' or '#' or '!';
    }

    // ^\s*(?:Include|LoadModule|SetEnv|Option|Alias)\s+\S+ with IGNORECASE via match() on a line.
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

    // ^\s*\[[^\]]*$ via match() on a line: '[' is the first non-space character and no ']' follows.
    private static bool IsMalformedSectionLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        return offset < line.Length && line[offset] == '[' && !line.AsSpan(offset + 1).Contains(']');
    }

    // ^\s*[{}]+\s*$ via match() on a line.
    private static bool IsStandaloneBraceLine(string line)
    {
        var stripped = PythonText.Strip(line);
        return stripped.Length > 0 && stripped.All(ch => ch is '{' or '}');
    }

    // ^\s*(?:LoadModule|SetEnv|<VirtualHost|<Directory|ServerName)\b with IGNORECASE | MULTILINE over the text. Each
    // whitespace run is skipped once: every line start inside it reaches the same offset (see LineStartMatcher).
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

    // ^\s*(?:server\s*\{|location\s+|upstream\s+) with IGNORECASE | MULTILINE over the text.
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
    /// Python <c>round(value, digits)</c> for a non-negative <paramref name="digits"/>: the exact binary value is rounded to the
    /// nearest multiple of 10^-digits with ties to even, and that decimal is read back as the nearest double, as
    /// <c>double_round</c> does through <c>_Py_dg_dtoa</c> and <c>_Py_dg_strtod</c>. <see cref="Math.Round(double, int)"/>
    /// rounds the scaled product instead, which differs whenever the multiplication itself rounds onto a midpoint. NaN and the
    /// infinities are returned as they are; a negative value rounds as its magnitude does, keeping its sign (so -0.0004 gives -0.0).
    /// </summary>
    internal static double PythonRound(double value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        if (!double.IsFinite(value) || value == 0.0)
        {
            return value;
        }

        if (value < 0)
        {
            return -PythonRound(-value, digits);
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
