using System.Buffers;
using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python <c>str</c> semantics that .NET does not share: the whitespace set of <c>str.isspace</c> / <c>re \s</c>
/// (adds U+001C-U+001F to <see cref="char.IsWhiteSpace(char)"/>), the word set of <c>re \w</c> on code points, the
/// full lowercase mapping of <c>str.lower</c> (U+0130 expands, final sigma is contextual) and the full uppercase
/// mapping of <c>str.upper</c> (one code point may become several).
/// </summary>
public static class PythonText
{
    // Every code point for which str.isspace() is true (Python 3.13); identical to the set matched by re \s.
    public static bool IsSpace(char ch) => ch switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' => true,
        '\x1c' or '\x1d' or '\x1e' or '\x1f' => true,
        ' ' or '\x85' or '\xa0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' => true,
        _ => false,
    };

    /// <summary><c>str.strip()</c> with no argument.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary><c>str.lstrip()</c> with no argument.</summary>
    public static string StripStart(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        while (start < text.Length && IsSpace(text[start]))
        {
            start++;
        }

        return start == 0 ? text : text[start..];
    }

    /// <summary><c>str.rstrip()</c> with no argument.</summary>
    public static string StripEnd(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = text.Length;
        while (end > 0 && IsSpace(text[end - 1]))
        {
            end--;
        }

        return end == text.Length ? text : text[..end];
    }

    /// <summary>
    /// <c>sub in text</c>: a code point subsequence, so an occurrence that would start or end inside a surrogate pair
    /// (the needle being or ending in a lone surrogate) does not count.
    /// </summary>
    public static bool Contains(string text, string sub)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sub);
        for (var start = text.IndexOf(sub, StringComparison.Ordinal); start >= 0; start = text.IndexOf(sub, start + 1, StringComparison.Ordinal))
        {
            var end = start + sub.Length;
            var splitsStart = start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]);
            var splitsEnd = end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]);
            if (!splitsStart && !splitsEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>str.split()</c> with no separator: runs of whitespace delimit, empties dropped.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        var start = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (IsSpace(text[index]))
            {
                if (start >= 0)
                {
                    words.Add(text[start..index]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = index;
            }
        }

        if (start >= 0)
        {
            words.Add(text[start..]);
        }

        return words;
    }

    /// <summary>
    /// <c>re \w</c> for str patterns: letters (L*), numbers (N*) and underscore, on whole code points, under CPython 3.13's Unicode 15.1
    /// categories (<see cref="PythonUnicode.GetCategory"/>).
    /// </summary>
    public static bool IsWordRune(Rune rune) => rune.Value == '_' || PythonUnicode.IsAlnum(rune.Value);

    /// <summary><c>str.isalnum()</c> of one code point under CPython 3.13's Unicode 15.1 tables: letters (L*) and numbers (N*).</summary>
    public static bool IsAlnum(Rune rune) => PythonUnicode.IsAlnum(rune.Value);

    /// <summary>
    /// <c>str.isprintable()</c> of one code point (<c>Py_UNICODE_ISPRINTABLE</c>) under CPython 3.13's Unicode 15.1 tables: every
    /// category but Cc, Cf, Cs, Co, Cn, Zl, Zp and Zs, with U+0020 printable.
    /// </summary>
    public static bool IsPrintable(int codePoint)
    {
        if (codePoint == ' ')
        {
            return true;
        }

        return PythonUnicode.GetCategory(codePoint) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    /// <summary>
    /// <c>str.lower()</c>: the simple lowercase mapping of every code point, the one unconditional SpecialCasing
    /// expansion (U+0130 to "i\u0307") and the Final_Sigma rule (U+03A3 becomes U+03C2 when a cased code point
    /// precedes it and none follows, skipping case-ignorable code points on both sides). An unpaired surrogate is a
    /// code point of its own to Python (category Cs, no case mapping) and passes through unchanged.
    /// </summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
            {
                builder.Append(text[offset]);
                offset++;
                continue;
            }

            var next = offset + consumed;
            if (rune.Value == 0x0130)
            {
                builder.Append("i\u0307");
            }
            else if (rune.Value == 0x03A3)
            {
                builder.Append(IsFinalSigma(text, offset, next) ? '\u03C2' : '\u03C3');
            }
            else
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }

            offset = next;
        }

        return builder.ToString();
    }

    // CPython handle_capital_sigma: cased before (skipping case-ignorables) and not cased after (likewise).
    private static bool IsFinalSigma(string text, int start, int end)
    {
        var offset = start;
        var casedBefore = false;
        while (offset > 0)
        {
            Rune.DecodeLastFromUtf16(text.AsSpan(0, offset), out var rune, out var consumed);
            offset -= consumed;
            if (!IsCaseIgnorable(rune))
            {
                casedBefore = IsCased(rune);
                break;
            }
        }

        if (!casedBefore)
        {
            return false;
        }

        offset = end;
        while (offset < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed);
            offset += consumed;
            if (!IsCaseIgnorable(rune))
            {
                return !IsCased(rune);
            }
        }

        return true;
    }

    // Case_Ignorable: Mn, Me, Cf, Lm, Sk plus Word_Break MidLetter, MidNumLet and Single_Quote. Verified against the
    // interpreter over every code point (a code point that is both cased and case-ignorable is skipped, as CPython
    // tests case-ignorable first).
    private static bool IsCaseIgnorable(Rune rune)
    {
        switch (PythonUnicode.GetCategory(rune.Value))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.ModifierSymbol:
                return true;
            default:
                return rune.Value is 0x27 or 0x2E or 0x3A or 0xB7 or 0x387 or 0x55F or 0x5F4 or 0x2018 or 0x2019 or 0x2024
                    or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A;
        }
    }

    // Cased: Lu, Ll, Lt plus the Other_Lowercase and Other_Uppercase code points that are not case-ignorable
    // (ordinal indicators, Roman numerals, circled and squared Latin letters).
    private static bool IsCased(Rune rune)
    {
        switch (PythonUnicode.GetCategory(rune.Value))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
                return true;
            default:
                return rune.Value is 0xAA or 0xBA
                    or (>= 0x2160 and <= 0x217F)
                    or (>= 0x24B6 and <= 0x24E9)
                    or (>= 0x1F130 and <= 0x1F149)
                    or (>= 0x1F150 and <= 0x1F169)
                    or (>= 0x1F170 and <= 0x1F189);
        }
    }

    /// <summary><c>str.upper()</c> of a single code point, including the SpecialCasing expansions (\u00DF to SS, \uFB01 to FI).</summary>
    public static string Upper(Rune rune)
        => FullUppercase.TryGetValue(rune.Value, out var expansion) ? expansion : Rune.ToUpperInvariant(rune).ToString();

    // Generated from the interpreter: every code point whose str.upper() is longer than one code point, plus the one
    // single-code-point mapping the invariant culture refuses (dotless i).
    private static readonly Dictionary<int, string> FullUppercase = new()
    {
        [0x0131] = "I",
        [0x00DF] = "SS",
        [0x0149] = "\u02BCN",
        [0x01F0] = "J\u030C",
        [0x0390] = "\u0399\u0308\u0301",
        [0x03B0] = "\u03A5\u0308\u0301",
        [0x0587] = "\u0535\u0552",
        [0x1E96] = "H\u0331",
        [0x1E97] = "T\u0308",
        [0x1E98] = "W\u030A",
        [0x1E99] = "Y\u030A",
        [0x1E9A] = "A\u02BE",
        [0x1F50] = "\u03A5\u0313",
        [0x1F52] = "\u03A5\u0313\u0300",
        [0x1F54] = "\u03A5\u0313\u0301",
        [0x1F56] = "\u03A5\u0313\u0342",
        [0x1F80] = "\u1F08\u0399",
        [0x1F81] = "\u1F09\u0399",
        [0x1F82] = "\u1F0A\u0399",
        [0x1F83] = "\u1F0B\u0399",
        [0x1F84] = "\u1F0C\u0399",
        [0x1F85] = "\u1F0D\u0399",
        [0x1F86] = "\u1F0E\u0399",
        [0x1F87] = "\u1F0F\u0399",
        [0x1F88] = "\u1F08\u0399",
        [0x1F89] = "\u1F09\u0399",
        [0x1F8A] = "\u1F0A\u0399",
        [0x1F8B] = "\u1F0B\u0399",
        [0x1F8C] = "\u1F0C\u0399",
        [0x1F8D] = "\u1F0D\u0399",
        [0x1F8E] = "\u1F0E\u0399",
        [0x1F8F] = "\u1F0F\u0399",
        [0x1F90] = "\u1F28\u0399",
        [0x1F91] = "\u1F29\u0399",
        [0x1F92] = "\u1F2A\u0399",
        [0x1F93] = "\u1F2B\u0399",
        [0x1F94] = "\u1F2C\u0399",
        [0x1F95] = "\u1F2D\u0399",
        [0x1F96] = "\u1F2E\u0399",
        [0x1F97] = "\u1F2F\u0399",
        [0x1F98] = "\u1F28\u0399",
        [0x1F99] = "\u1F29\u0399",
        [0x1F9A] = "\u1F2A\u0399",
        [0x1F9B] = "\u1F2B\u0399",
        [0x1F9C] = "\u1F2C\u0399",
        [0x1F9D] = "\u1F2D\u0399",
        [0x1F9E] = "\u1F2E\u0399",
        [0x1F9F] = "\u1F2F\u0399",
        [0x1FA0] = "\u1F68\u0399",
        [0x1FA1] = "\u1F69\u0399",
        [0x1FA2] = "\u1F6A\u0399",
        [0x1FA3] = "\u1F6B\u0399",
        [0x1FA4] = "\u1F6C\u0399",
        [0x1FA5] = "\u1F6D\u0399",
        [0x1FA6] = "\u1F6E\u0399",
        [0x1FA7] = "\u1F6F\u0399",
        [0x1FA8] = "\u1F68\u0399",
        [0x1FA9] = "\u1F69\u0399",
        [0x1FAA] = "\u1F6A\u0399",
        [0x1FAB] = "\u1F6B\u0399",
        [0x1FAC] = "\u1F6C\u0399",
        [0x1FAD] = "\u1F6D\u0399",
        [0x1FAE] = "\u1F6E\u0399",
        [0x1FAF] = "\u1F6F\u0399",
        [0x1FB2] = "\u1FBA\u0399",
        [0x1FB3] = "\u0391\u0399",
        [0x1FB4] = "\u0386\u0399",
        [0x1FB6] = "\u0391\u0342",
        [0x1FB7] = "\u0391\u0342\u0399",
        [0x1FBC] = "\u0391\u0399",
        [0x1FC2] = "\u1FCA\u0399",
        [0x1FC3] = "\u0397\u0399",
        [0x1FC4] = "\u0389\u0399",
        [0x1FC6] = "\u0397\u0342",
        [0x1FC7] = "\u0397\u0342\u0399",
        [0x1FCC] = "\u0397\u0399",
        [0x1FD2] = "\u0399\u0308\u0300",
        [0x1FD3] = "\u0399\u0308\u0301",
        [0x1FD6] = "\u0399\u0342",
        [0x1FD7] = "\u0399\u0308\u0342",
        [0x1FE2] = "\u03A5\u0308\u0300",
        [0x1FE3] = "\u03A5\u0308\u0301",
        [0x1FE4] = "\u03A1\u0313",
        [0x1FE6] = "\u03A5\u0342",
        [0x1FE7] = "\u03A5\u0308\u0342",
        [0x1FF2] = "\u1FFA\u0399",
        [0x1FF3] = "\u03A9\u0399",
        [0x1FF4] = "\u038F\u0399",
        [0x1FF6] = "\u03A9\u0342",
        [0x1FF7] = "\u03A9\u0342\u0399",
        [0x1FFC] = "\u03A9\u0399",
        [0xFB00] = "FF",
        [0xFB01] = "FI",
        [0xFB02] = "FL",
        [0xFB03] = "FFI",
        [0xFB04] = "FFL",
        [0xFB05] = "ST",
        [0xFB06] = "ST",
        [0xFB13] = "\u0544\u0546",
        [0xFB14] = "\u0544\u0535",
        [0xFB15] = "\u0544\u053B",
        [0xFB16] = "\u054E\u0546",
        [0xFB17] = "\u0544\u053D",
    };
}
