using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary><c>str.isidentifier()</c> from the general categories behind XID_Start and XID_Continue.</summary>
/// <remarks>
/// ID_Start is L*, Nl and Other_ID_Start; ID_Continue adds Mn, Mc, Nd, Pc and Other_ID_Continue. The XID forms drop a
/// handful of code points whose NFKC form is not an identifier (U+037A, U+0E33, U+FF9E and similar); those are accepted
/// here, which only matters for a group name spelled with one of them.
/// </remarks>
internal static class EngineIdentifier
{
    public static bool IsIdentifier(string text)
    {
        var codes = ReTokenizer.CodePoints(text);
        if (codes.Length == 0 || !IsStart(codes[0]))
        {
            return false;
        }

        return codes.Skip(1).All(IsContinue);
    }

    private static bool IsStart(int code)
    {
        if (code == '_' || code is 0x2118 or 0x212E or 0x309B or 0x309C or 0x1885 or 0x1886)
        {
            return true;
        }

        return Category(code) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.LetterNumber;
    }

    private static bool IsContinue(int code)
    {
        if (IsStart(code) || code is 0x00B7 or 0x0387 or (>= 0x1369 and <= 0x1371) or 0x19DA or 0x200C or 0x200D or 0x30FB or 0xFF65)
        {
            return true;
        }

        return Category(code) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
    }

    private static UnicodeCategory Category(int code) => EngineUnicode.GetCategory(code);
}
