using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>Character classes the plugins' linear scanners test code point by code point.</summary>
public static class RuneText
{
    extension(Rune rune)
    {
        /// <summary>A character .NET's regular-expression <c>\w</c> matches: <c>[\p{L}\p{Mn}\p{Nd}\p{Pc}]</c>.</summary>
        public bool IsWordCharacter => Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
    }
}
