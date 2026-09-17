using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The Unicode character database CPython 3.13 carries (Unicode 15.1), over the runtime's tables (.NET 10 carries Unicode 16.0):
/// the general category, the decimal and digit values, and the <c>str</c> predicates built on them. The runtime's values are used
/// except where the two versions differ: the code points first assigned in 16.0 (<see cref="Unicode16Assignments"/>), which are Cn
/// with no value under 15.1, and the one code point whose category changed.
/// </summary>
/// <remarks>
/// <c>EngineUnicodeTableTests</c> and <c>EngineReCaseTests</c> compare the category, decimal and digit values, <c>str.isalpha()</c>,
/// <c>str.isalnum()</c> and <c>str.isprintable()</c> with CPython's over sampled code point windows
/// (<c>Data/regex_cases.json</c>); when the runtime's tables change inside them, the category test fails with the delta. Numeric values (<c>str.isnumeric()</c>)
/// are not covered: the runtime lacks the Unihan numeric values of some ideographs, and nothing here reads them.
/// </remarks>
public static class EngineUnicode
{
    // Every code point .NET 10's tables assign that CPython 3.13's leave unassigned (Cn), as inclusive ranges in code point order. The
    // reverse set is empty.
    private static readonly (int Start, int End)[] Unicode16Assignments =
    [
        (0x0897, 0x0897), (0x1B4E, 0x1B4F), (0x1B7F, 0x1B7F), (0x1C89, 0x1C8A), (0x2427, 0x2429), (0x31E4, 0x31E5), (0xA7CB, 0xA7CD),
        (0xA7DA, 0xA7DC), (0x105C0, 0x105F3), (0x10D40, 0x10D65), (0x10D69, 0x10D85), (0x10D8E, 0x10D8F), (0x10EC2, 0x10EC4),
        (0x10EFC, 0x10EFC), (0x11380, 0x11389), (0x1138B, 0x1138B), (0x1138E, 0x1138E), (0x11390, 0x113B5), (0x113B7, 0x113C0),
        (0x113C2, 0x113C2), (0x113C5, 0x113C5), (0x113C7, 0x113CA), (0x113CC, 0x113D5), (0x113D7, 0x113D8), (0x113E1, 0x113E2),
        (0x116D0, 0x116E3), (0x11BC0, 0x11BE1), (0x11BF0, 0x11BF9), (0x11F5A, 0x11F5A), (0x13460, 0x143FA), (0x16100, 0x16139),
        (0x16D40, 0x16D79), (0x18CFF, 0x18CFF), (0x1CC00, 0x1CCF9), (0x1CD00, 0x1CEB3), (0x1E5D0, 0x1E5FA), (0x1E5FF, 0x1E5FF),
        (0x1F8B2, 0x1F8BB), (0x1F8C0, 0x1F8C1), (0x1FA89, 0x1FA89), (0x1FA8F, 0x1FA8F), (0x1FABE, 0x1FABE), (0x1FAC6, 0x1FAC6),
        (0x1FADC, 0x1FADC), (0x1FADF, 0x1FADF), (0x1FAE9, 0x1FAE9), (0x1FBCB, 0x1FBEF),
    ];

    // Code points whose category Unicode 16.0 changed: U+1171E AHOM CONSONANT SIGN MEDIAL RA is Mn under 15.1 and Mc under 16.0.
    private static readonly Dictionary<int, UnicodeCategory> Unicode15Categories = new()
    {
        [0x1171E] = UnicodeCategory.NonSpacingMark,
    };

    /// <summary>The code points <see cref="GetCategory"/> reads as unassigned although the runtime assigns them.</summary>
    internal static IReadOnlyList<(int Start, int End)> RuntimeOnlyAssignments => Unicode16Assignments;

    /// <summary>The code points whose <see cref="GetCategory"/> is not the runtime's category for another reason.</summary>
    internal static IReadOnlyDictionary<int, UnicodeCategory> ChangedCategories => Unicode15Categories;

    /// <summary><c>unicodedata.category(chr(code_point))</c>: a surrogate code point is Cs.</summary>
    public static UnicodeCategory GetCategory(int codePoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(codePoint);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codePoint, 0x10FFFF);
        if (codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return UnicodeCategory.Surrogate;
        }

        if (Unicode15Categories.TryGetValue(codePoint, out var category))
        {
            return category;
        }

        return InRanges(Unicode16Assignments, codePoint) ? UnicodeCategory.OtherNotAssigned : CharUnicodeInfo.GetUnicodeCategory(codePoint);
    }

    /// <summary><c>unicodedata.decimal(chr(code_point), -1)</c> (<c>Py_UNICODE_TODECIMAL</c>): the value of a decimal digit (Nd), else -1.</summary>
    public static int DecimalValue(int codePoint)
        => GetCategory(codePoint) == UnicodeCategory.DecimalDigitNumber ? CharUnicodeInfo.GetDecimalDigitValue(char.ConvertFromUtf32(codePoint), 0) : -1;

    /// <summary><c>unicodedata.digit(chr(code_point), -1)</c> (<c>Py_UNICODE_TODIGIT</c>): decimal digits plus the other digits (superscripts, circled digits).</summary>
    public static int DigitValue(int codePoint)
        => GetCategory(codePoint) is UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate
            ? -1
            : CharUnicodeInfo.GetDigitValue(char.ConvertFromUtf32(codePoint), 0);

    /// <summary><c>str.isdecimal()</c> of one code point.</summary>
    public static bool IsDecimal(int codePoint) => DecimalValue(codePoint) >= 0;

    /// <summary><c>str.isdigit()</c> of one code point.</summary>
    public static bool IsDigit(int codePoint) => DigitValue(codePoint) >= 0;

    /// <summary><c>str.isalpha()</c> of one code point: the letter categories (L*).</summary>
    public static bool IsAlpha(int codePoint) => GetCategory(codePoint) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    /// <summary>
    /// <c>str.isalnum()</c> of one code point: <c>isalpha</c>, <c>isdecimal</c>, <c>isdigit</c> or <c>isnumeric</c>, which over the
    /// interpreter's tables is exactly the letter and number categories (L*, N*).
    /// </summary>
    public static bool IsAlnum(int codePoint) => IsAlpha(codePoint) || GetCategory(codePoint) is UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    private static bool InRanges((int Start, int End)[] ranges, int codePoint)
    {
        var low = 0;
        var high = ranges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var (start, end) = ranges[middle];
            if (codePoint < start)
            {
                high = middle - 1;
            }
            else if (codePoint > end)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
