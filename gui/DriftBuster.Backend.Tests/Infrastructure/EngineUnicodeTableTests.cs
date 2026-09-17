using System.Globalization;
using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="EngineUnicode"/> against CPython 3.13's <c>unicodedata</c> (Unicode 15.1) over the code point windows sampled in
/// <c>Data/regex_cases.json</c>.
/// </summary>
public sealed class EngineUnicodeTableTests
{
    private static readonly string[] CategoryNames =
    [
        "Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc", "Cf", "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi",
        "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn",
    ];

    private static OrderedDictionary<string, object?> Data => EngineReCaseTests.Data.Value;

    private static IEnumerable<List<object?>> Runs(string section) => ((List<object?>)Data[section]!).Cast<List<object?>>();

    // unicodedata.category over the sampled code points, expanded from its runs (null elsewhere).
    private static string?[] RecordedCategories()
    {
        var categories = new string?[0x110000];
        foreach (var run in Runs("categories"))
        {
            Array.Fill(categories, (string)run[2]!, (int)run[0]!, (int)run[1]! - (int)run[0]! + 1);
        }

        return categories;
    }

    // A value lookup over the sampled code points (-1 where there is none), expanded from [first, last, value of first] runs.
    private static int[] EngineValues(string section)
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
    public void CategoryMatchesUnicodedataOnTheSampledCodePoints()
    {
        var expected = RecordedCategories();
        var mismatches = new List<string>();
        foreach (var code in EngineReCaseTests.SampledCodePoints())
        {
            if (!string.Equals(CategoryNames[(int)EngineUnicode.GetCategory(code)], expected[code], StringComparison.Ordinal))
            {
                mismatches.Add($"U+{code:X4}: {CategoryNames[(int)EngineUnicode.GetCategory(code)]} != {expected[code]}");
            }
        }

        mismatches.Should().BeEmpty($"the tables EngineUnicode should hold are:{Environment.NewLine}{DeltaTables(expected)}");
    }

    // The delta between the runtime's categories and the interpreter's, spelled as EngineUnicode's two tables.
    private static string DeltaTables(string?[] expected)
    {
        var assignments = new List<(int Start, int End)>();
        var changed = new StringBuilder();
        foreach (var code in EngineReCaseTests.SampledCodePoints())
        {
            var runtime = RuntimeCategory(code);
            if (string.Equals(runtime, expected[code], StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(expected[code], "Cn", StringComparison.Ordinal))
            {
                changed.Append(CultureInfo.InvariantCulture, $"[0x{code:X4}] = {expected[code]}, ");
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
        var expected = RecordedCategories();
        foreach (var (start, end) in EngineUnicode.RuntimeOnlyAssignments)
        {
            for (var code = start; code <= end; code++)
            {
                if (expected[code] is null)
                {
                    continue;
                }

                RuntimeCategory(code).Should().NotBe("Cn", $"U+{code:X4} is assigned by the runtime");
                expected[code].Should().Be("Cn");
            }
        }

        foreach (var (code, category) in EngineUnicode.ChangedCategories)
        {
            RuntimeCategory(code).Should().NotBe(CategoryNames[(int)category]);
        }
    }

    [Fact]
    public void DecimalAndDigitValuesMatchUnicodedataOnTheSampledCodePoints()
    {
        var decimals = EngineValues("decimal");
        var digits = EngineValues("digit");
        var mismatches = new List<string>();
        foreach (var code in EngineReCaseTests.SampledCodePoints())
        {
            if (EngineUnicode.DecimalValue(code) != decimals[code] || EngineUnicode.IsDecimal(code) != decimals[code] >= 0)
            {
                mismatches.Add($"decimal U+{code:X4}: {EngineUnicode.DecimalValue(code)} != {decimals[code]}");
            }

            if (EngineUnicode.DigitValue(code) != digits[code] || EngineUnicode.IsDigit(code) != digits[code] >= 0)
            {
                mismatches.Add($"digit U+{code:X4}: {EngineUnicode.DigitValue(code)} != {digits[code]}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsAlphaMatchesStrIsAlphaOnTheSampledCodePoints()
    {
        var alpha = CodePointSet.FromRanges(Runs("alpha").Select(pair => ((int)pair[0]!, (int)pair[1]!)));
        var mismatches = new List<string>();
        foreach (var code in EngineReCaseTests.SampledCodePoints())
        {
            if (EngineUnicode.IsAlpha(code) != alpha.Contains(code))
            {
                mismatches.Add($"U+{code:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }
}
