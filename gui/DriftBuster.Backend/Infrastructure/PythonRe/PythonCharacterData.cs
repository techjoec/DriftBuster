using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>
/// The character predicates and case mappings CPython's <c>_sre</c> applies to str patterns, as code point sets.
/// </summary>
/// <remarks>
/// <c>sre_lower_unicode</c> and <c>sre_upper_unicode</c> are <c>_PyUnicode_ToLowercase</c> / <c>_PyUnicode_ToUppercase</c>,
/// which return the first code point of the full mapping when SpecialCasing gives one (U+0130 lowers to "i", U+00DF uppers
/// to "S"). <c>PythonReOracleTests</c> checks the lowercase table and the cased set against the interpreter over every
/// code point, and the category sets through the atom oracle. No code point at or above U+20000 changes case, so the change
/// tables stop there.
/// </remarks>
internal static class PythonCharacterData
{
    private const int CaseScanLimit = 0x20000;

    private static readonly Lazy<Dictionary<int, int>> LowerChanges = new(() => BuildChanges(LowerCore));
    private static readonly Lazy<Dictionary<int, int>> UpperChanges = new(() => BuildChanges(UpperCore));
    private static readonly Lazy<CodePointSet> LowerChangedSet = new(() => CodePointSet.FromCodePoints(LowerChanges.Value.Keys));
    private static readonly Lazy<CodePointSet> UpperChangedSet = new(() => CodePointSet.FromCodePoints(UpperChanges.Value.Keys));
    private static readonly Lazy<CodePointSet> UnicodeWordSet = new(() => CodePointSet.FromPredicate(IsUnicodeWord));
    private static readonly Lazy<CodePointSet> UnicodeDigitSet = new(() => CodePointSet.FromPredicate(IsUnicodeDigit));
    private static readonly Lazy<CodePointSet> UnicodeSpaceSet = new(() => CodePointSet.FromPredicate(code => code <= 0xFFFF && !IsSurrogate(code) && PythonText.IsSpace((char)code)));

    /// <summary><c>re._casefix._EXTRA_CASES</c>: lowercase code points mapped to the other lowercase code points sharing their uppercase.</summary>
    public static readonly IReadOnlyDictionary<int, int[]> ExtraCases = new Dictionary<int, int[]>
    {
        [0x0069] = [0x0131],
        [0x0073] = [0x017F],
        [0x00B5] = [0x03BC],
        [0x0131] = [0x0069],
        [0x017F] = [0x0073],
        [0x0345] = [0x03B9, 0x1FBE],
        [0x0390] = [0x1FD3],
        [0x03B0] = [0x1FE3],
        [0x03B2] = [0x03D0],
        [0x03B5] = [0x03F5],
        [0x03B8] = [0x03D1],
        [0x03B9] = [0x0345, 0x1FBE],
        [0x03BA] = [0x03F0],
        [0x03BC] = [0x00B5],
        [0x03C0] = [0x03D6],
        [0x03C1] = [0x03F1],
        [0x03C2] = [0x03C3],
        [0x03C3] = [0x03C2],
        [0x03C6] = [0x03D5],
        [0x03D0] = [0x03B2],
        [0x03D1] = [0x03B8],
        [0x03D5] = [0x03C6],
        [0x03D6] = [0x03C0],
        [0x03F0] = [0x03BA],
        [0x03F1] = [0x03C1],
        [0x03F5] = [0x03B5],
        [0x0432] = [0x1C80],
        [0x0434] = [0x1C81],
        [0x043E] = [0x1C82],
        [0x0441] = [0x1C83],
        [0x0442] = [0x1C84, 0x1C85],
        [0x044A] = [0x1C86],
        [0x0463] = [0x1C87],
        [0x1C80] = [0x0432],
        [0x1C81] = [0x0434],
        [0x1C82] = [0x043E],
        [0x1C83] = [0x0441],
        [0x1C84] = [0x0442, 0x1C85],
        [0x1C85] = [0x0442, 0x1C84],
        [0x1C86] = [0x044A],
        [0x1C87] = [0x0463],
        [0x1C88] = [0xA64B],
        [0x1E61] = [0x1E9B],
        [0x1E9B] = [0x1E61],
        [0x1FBE] = [0x0345, 0x03B9],
        [0x1FD3] = [0x0390],
        [0x1FE3] = [0x03B0],
        [0xA64B] = [0x1C88],
        [0xFB05] = [0xFB06],
        [0xFB06] = [0xFB05],
    };

    public static CodePointSet Word => UnicodeWordSet.Value;

    public static CodePointSet Digit => UnicodeDigitSet.Value;

    public static CodePointSet Space => UnicodeSpaceSet.Value;

    /// <summary><c>SRE_IS_WORD</c> for the ASCII flag: ASCII letters, digits and underscore.</summary>
    public static CodePointSet AsciiWord { get; } = CodePointSet.FromRanges([('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z')]);

    public static CodePointSet AsciiDigit { get; } = CodePointSet.Range('0', '9');

    /// <summary><c>SRE_IS_SPACE</c>: <c>Py_ISSPACE</c> below 128, which is \t \n \v \f \r and space.</summary>
    public static CodePointSet AsciiSpace { get; } = CodePointSet.FromRanges([(0x09, 0x0D), (0x20, 0x20)]);

    public static bool IsSurrogate(int code) => code is >= 0xD800 and <= 0xDFFF;

    /// <summary><c>sre_lower_unicode</c>.</summary>
    public static int Lower(int code) => LowerChanges.Value.TryGetValue(code, out var lower) ? lower : code;

    /// <summary><c>sre_upper_unicode</c>.</summary>
    public static int Upper(int code) => UpperChanges.Value.TryGetValue(code, out var upper) ? upper : code;

    /// <summary><c>_sre.unicode_iscased</c>.</summary>
    public static bool IsCased(int code) => Lower(code) != code || Upper(code) != code;

    /// <summary><c>sre_lower_ascii</c>.</summary>
    public static int AsciiLower(int code) => code is >= 'A' and <= 'Z' ? code + 32 : code;

    /// <summary><c>_sre.ascii_iscased</c>.</summary>
    public static bool AsciiIsCased(int code) => code is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    /// <summary>Every code point whose <see cref="Lower"/> lies in <paramref name="targets"/>.</summary>
    public static CodePointSet LowerPreimage(CodePointSet targets) => Preimage(targets, LowerChanges.Value, LowerChangedSet.Value);

    /// <summary>Every code point whose <see cref="Upper"/> lies in <paramref name="targets"/>.</summary>
    public static CodePointSet UpperPreimage(CodePointSet targets) => Preimage(targets, UpperChanges.Value, UpperChangedSet.Value);

    /// <summary>Every code point whose <see cref="AsciiLower"/> lies in <paramref name="targets"/>.</summary>
    public static CodePointSet AsciiLowerPreimage(CodePointSet targets)
    {
        var upper = CodePointSet.Range('A', 'Z');
        var mapped = upper.CodePoints().Where(code => targets.Contains(code + 32));
        return targets.Except(upper).Union(CodePointSet.FromCodePoints(mapped));
    }

    private static CodePointSet Preimage(CodePointSet targets, Dictionary<int, int> changes, CodePointSet changed)
    {
        var mapped = changes.Where(pair => targets.Contains(pair.Value)).Select(pair => pair.Key);
        return targets.Except(changed).Union(CodePointSet.FromCodePoints(mapped));
    }

    private static Dictionary<int, int> BuildChanges(Func<Rune, int> map)
    {
        var changes = new Dictionary<int, int>();
        for (var code = 0; code < CaseScanLimit; code++)
        {
            if (IsSurrogate(code) || !MayChangeCase(Rune.GetUnicodeCategory(new Rune(code))))
            {
                continue;
            }

            var mapped = map(new Rune(code));
            if (mapped != code)
            {
                changes[code] = mapped;
            }
        }

        return changes;
    }

    // Case mappings exist only for cased letters, U+0345 (Mn), the Roman numerals (Nl) and the circled Latin letters (So);
    // skipping every other category keeps the one-time scan cheap. The oracle tests compare the result over every code point.
    private static bool MayChangeCase(UnicodeCategory category) => category is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherSymbol;

    private static int LowerCore(Rune rune) => rune.Value == 0x0130 ? 0x0069 : Rune.ToLowerInvariant(rune).Value;

    private static int UpperCore(Rune rune) => Rune.GetRuneAt(PythonText.Upper(rune), 0).Value;

    // Py_UNICODE_ISALNUM or '_': isalpha (L*), isdecimal, isdigit and isnumeric, which over Unicode's categories are the
    // letters and the three number categories (PythonText.IsWordRune).
    private static bool IsUnicodeWord(int code) => !IsSurrogate(code) && PythonText.IsWordRune(new Rune(code));

    // Py_UNICODE_ISDECIMAL: category Nd.
    private static bool IsUnicodeDigit(int code)
        => !IsSurrogate(code) && Rune.GetUnicodeCategory(new Rune(code)) == UnicodeCategory.DecimalDigitNumber;
}
