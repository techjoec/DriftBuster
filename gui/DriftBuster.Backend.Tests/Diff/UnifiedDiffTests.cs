using DriftBuster.Backend.Diff;

using static DriftBuster.Backend.Tests.Diff.DiffOracleData;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// <see cref="UnifiedDiff"/> byte-for-byte against CPython 3.13 <c>difflib.unified_diff</c> on the inputs in
/// <c>Data/difflib_cases.json</c>: <c>lineterm=""</c> joined with LF for n = -1, 0, 1, 3 and 5 and the contexts at +-2^30 and +-2^31 where
/// <c>get_grouped_opcodes</c> needs integers past 32 bits, and the default
/// <c>lineterm="\n"</c> with a from-file date, joined with nothing.
/// </summary>
public sealed class UnifiedDiffTests
{
    private const string DataFile = "difflib_cases.json";

    public static TheoryData<string> CaseNames => Names(DataFile);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void LinesMatchDifflibWithEmptyLineTerm(string name)
    {
        var entry = Case(DataFile, name);
        var a = Strings(entry["a"]);
        var b = Strings(entry["b"]);
        foreach (var (context, expected) in Map(entry["unified"]))
        {
            var n = int.Parse(context, System.Globalization.CultureInfo.InvariantCulture);
            var actual = string.Join("\n", UnifiedDiff.Lines(a, b, "left.cfg", "right.cfg", lineTerm: string.Empty, n: n));
            actual.Should().Be((string)expected!, "n={0}", n);
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void LinesMatchDifflibWithDefaultLineTermAndDate(string name)
    {
        var entry = Case(DataFile, name);
        var actual = string.Concat(UnifiedDiff.Lines(Strings(entry["a"]), Strings(entry["b"]), "a", "b", fromFileDate: "2026-01-01"));
        actual.Should().Be((string)entry["unified_default"]!);
    }

    [Theory]
    [InlineData(0, 0, "0,0")]
    [InlineData(0, 1, "1")]
    [InlineData(3, 4, "4")]
    [InlineData(3, 3, "3,0")]
    [InlineData(2, 7, "3,5")]
    public void FormatRangeMatchesDifflib(int start, int stop, string expected)
    {
        UnifiedDiff.FormatRange(start, stop).Should().Be(expected);
    }

    [Fact]
    public void CalculateStatsCountsTheLongerSideOfReplacements()
    {
        UnifiedDiff.CalculateStats(["a", "b", "c"], ["x", "y", "c", "d"]).Should().Be(new DiffStats(1, 0, 2));
        UnifiedDiff.CalculateStats([], []).Should().Be(new DiffStats(0, 0, 0));
        UnifiedDiff.CalculateStats(["a", "b"], []).Should().Be(new DiffStats(0, 2, 0));
    }
}
