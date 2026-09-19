using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Unicode properties from the runtime's database (<see cref="CharUnicodeInfo"/>): category, digit values and predicates.
/// A runtime upgrade can change the answers with its Unicode version.
/// </summary>
public static class EngineUnicode
{
    /// <summary>The Unicode general category of a code point; a surrogate code point is <see cref="UnicodeCategory.Surrogate"/>.</summary>
    public static UnicodeCategory GetCategory(int codePoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(codePoint);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codePoint, 0x10FFFF);
        return codePoint is >= 0xD800 and <= 0xDFFF ? UnicodeCategory.Surrogate : CharUnicodeInfo.GetUnicodeCategory(codePoint);
    }

    /// <summary>The value of a decimal digit (category Nd), else -1.</summary>
    public static int DecimalValue(int codePoint)
        => GetCategory(codePoint) == UnicodeCategory.DecimalDigitNumber ? CharUnicodeInfo.GetDecimalDigitValue(char.ConvertFromUtf32(codePoint), 0) : -1;

    public static bool IsAlpha(int codePoint) => GetCategory(codePoint) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    public static bool IsAlnum(int codePoint) => IsAlpha(codePoint) || GetCategory(codePoint) is UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
}
