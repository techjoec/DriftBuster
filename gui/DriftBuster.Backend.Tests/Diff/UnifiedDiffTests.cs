using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary><see cref="UnifiedDiff"/> hunk ranges and change statistics.</summary>
public sealed class UnifiedDiffTests
{
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
