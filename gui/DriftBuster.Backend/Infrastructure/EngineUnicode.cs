using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The character properties the detectors and the text helpers read, over the Unicode character database the .NET runtime carries
/// (<see cref="CharUnicodeInfo"/>): the general category, the decimal and digit values, and the predicates built on them.
/// </summary>
/// <remarks>
/// The tables are the runtime's, so a runtime upgrade moves these answers with the Unicode version it carries.
/// </remarks>
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

    /// <summary>Whether the code point is a letter (the L* categories).</summary>
    public static bool IsAlpha(int codePoint) => GetCategory(codePoint) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    /// <summary>Whether the code point is a letter or a number (the L* and N* categories).</summary>
    public static bool IsAlnum(int codePoint) => IsAlpha(codePoint) || GetCategory(codePoint) is UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
}
