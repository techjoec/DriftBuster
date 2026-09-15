using System.Globalization;
using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonUnicode"/> against CPython 3.13's <c>unicodedata</c> (Unicode 15.1) over every code point, from
/// <c>Data/python_regex_cases.json</c> (written by <c>tools/parity/gen_regex_cases.py</c>).
/// </summary>
public sealed class PythonUnicodeOracleTests
{
    private static readonly string[] CategoryNames =
    [
        "Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc", "Cf", "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi",
        "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn",
    ];

    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Infrastructure", "Data", "python_regex_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)value!;
    });

    private static IEnumerable<List<object?>> Runs(string section) => ((List<object?>)Data.Value[section]!).Cast<List<object?>>();

    // unicodedata.category over every code point, expanded from its runs.
    private static string[] PythonCategories()
    {
        var categories = new string[0x110000];
        foreach (var run in Runs("categories"))
        {
            Array.Fill(categories, (string)run[2]!, (int)run[0]!, (int)run[1]! - (int)run[0]! + 1);
        }

        return categories;
    }

    // A value lookup over every code point (-1 where there is none), expanded from [first, last, value of first] runs.
    private static int[] PythonValues(string section)
    {
        var values = new int[0x110000];
        Array.Fill(values, -1);
        foreach (var run in Runs(section))
        {
            for (var code = (int)run[0]!; code <= (int)run[1]!; code++)
            {
                values[code] = (int)run[2]! + code - (int)run[0]!;
            }
        }

        return values;
    }

    private static string RuntimeCategory(int code)
        => code is >= 0xD800 and <= 0xDFFF ? "Cs" : CategoryNames[(int)CharUnicodeInfo.GetUnicodeCategory(code)];

    [Fact]
    public void CategoryMatchesUnicodedataOnEveryCodePoint()
    {
        var python = PythonCategories();
        var mismatches = new List<string>();
        for (var code = 0; code <= 0x10FFFF; code++)
        {
            if (!string.Equals(CategoryNames[(int)PythonUnicode.GetCategory(code)], python[code], StringComparison.Ordinal))
            {
                mismatches.Add($"U+{code:X4}: {CategoryNames[(int)PythonUnicode.GetCategory(code)]} != {python[code]}");
            }
        }

        mismatches.Should().BeEmpty($"the tables PythonUnicode should hold are:{Environment.NewLine}{DeltaTables(python)}");
    }

    // The delta between the runtime's categories and the interpreter's, spelled as PythonUnicode's two tables.
    private static string DeltaTables(string[] python)
    {
        var assignments = new List<(int Start, int End)>();
        var changed = new StringBuilder();
        for (var code = 0; code <= 0x10FFFF; code++)
        {
            var runtime = RuntimeCategory(code);
            if (string.Equals(runtime, python[code], StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(python[code], "Cn", StringComparison.Ordinal))
            {
                changed.Append(CultureInfo.InvariantCulture, $"[0x{code:X4}] = {python[code]}, ");
            }
            else if (assignments.Count > 0 && assignments[^1].End + 1 == code)
            {
                assignments[^1] = (assignments[^1].Start, code);
            }
            else
            {
                assignments.Add((code, code));
            }
        }

        return string.Join(", ", assignments.Select(range => $"(0x{range.Start:X4}, 0x{range.End:X4})")) + Environment.NewLine + changed;
    }

    [Fact]
    public void TheDeltaTablesHoldOnlyWhatTheRuntimeGetsWrong()
    {
        var python = PythonCategories();
        foreach (var (start, end) in PythonUnicode.RuntimeOnlyAssignments)
        {
            for (var code = start; code <= end; code++)
            {
                RuntimeCategory(code).Should().NotBe("Cn", $"U+{code:X4} is assigned by the runtime");
                python[code].Should().Be("Cn");
            }
        }

        foreach (var (code, category) in PythonUnicode.ChangedCategories)
        {
            RuntimeCategory(code).Should().NotBe(CategoryNames[(int)category]);
        }
    }

    [Fact]
    public void DecimalAndDigitValuesMatchUnicodedataOnEveryCodePoint()
    {
        var decimals = PythonValues("decimal");
        var digits = PythonValues("digit");
        var mismatches = new List<string>();
        for (var code = 0; code <= 0x10FFFF; code++)
        {
            if (PythonUnicode.DecimalValue(code) != decimals[code] || PythonUnicode.IsDecimal(code) != decimals[code] >= 0)
            {
                mismatches.Add($"decimal U+{code:X4}: {PythonUnicode.DecimalValue(code)} != {decimals[code]}");
            }

            if (PythonUnicode.DigitValue(code) != digits[code] || PythonUnicode.IsDigit(code) != digits[code] >= 0)
            {
                mismatches.Add($"digit U+{code:X4}: {PythonUnicode.DigitValue(code)} != {digits[code]}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsAlphaMatchesStrIsAlphaOnEveryCodePoint()
    {
        var alpha = CodePointSet.FromRanges(Runs("alpha").Select(pair => ((int)pair[0]!, (int)pair[1]!)));
        var mismatches = new List<string>();
        for (var code = 0; code <= 0x10FFFF; code++)
        {
            if (PythonUnicode.IsAlpha(code) != alpha.Contains(code))
            {
                mismatches.Add($"U+{code:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\U0001CCF2")]
    [InlineData("\U00011BF0")]
    [InlineData("\U00016D70")]
    public void IntAndFloatRefuseTheDecimalDigitsFirstAssignedInUnicode16(string text)
    {
        var parseInt = () => PythonBuiltins.Int(text);
        parseInt.Should().Throw<PythonValueException>().WithMessage($"invalid literal for int() with base 10: '\\U{char.ConvertToUtf32(text, 0):x8}'");
        var parseFloat = () => PythonBuiltins.Float(text);
        parseFloat.Should().Throw<PythonValueException>();
        PythonBuiltins.Int("\u0663\U0001D7CE").Should().Be(30);
    }

    [Fact]
    public void StrFormatReadsOnlyTheDecimalDigitsOfUnicode151()
    {
        PlaceholderFormatter.Format("{token_name:\u0663}", "x").Should().Be("x  ");
        var field = () => PlaceholderFormatter.Format("{\U0001CCF1}", "x");
        field.Should().Throw<KeyNotFoundException>().WithMessage("'\\U0001ccf1'");
        var index = () => PlaceholderFormatter.Format("{\u0661}", "x");
        index.Should().Throw<StrFormatIndexException>().WithMessage("Replacement index 1 out of range for positional args tuple");
        var spec = () => PlaceholderFormatter.Format("{token_name:\U0001CCF1}", "x");
        spec.Should().Throw<FormatException>().WithMessage("Unknown format code '\\x1ccf1' for object of type 'str'");
    }
}
