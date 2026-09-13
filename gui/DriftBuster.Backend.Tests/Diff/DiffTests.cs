using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Mirror of tests/reporting/test_diff.py.</summary>
[Collection(DiffSafetyLimitsCollection.Name)]
public sealed class DiffTests
{
    private sealed class MaskingRedactor : RedactionFilter
    {
        public override string Apply(string text) => text.Replace("secret", "[MASK]", StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicaliseHelpersHandleEmptyPayloads()
    {
        Canonicaliser.CanonicaliseText(string.Empty).Should().BeEmpty();
        Canonicaliser.CanonicaliseXml(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void BuildUnifiedDiffAppliesCustomRedactor()
    {
        var result = DiffBuilder.BuildUnifiedDiff(
            "secret=1\nvalue=2\n",
            "secret=3\nvalue=2\n",
            contentType: "text",
            redactor: new MaskingRedactor(),
            contextLines: 1);

        result.Should().BeOfType<DiffArtifact>();
        result.Diff.Should().Contain("[MASK]");
        result.Diff.Should().NotContain("secret");
    }

    [Fact]
    public void BuildUnifiedDiffRejectsUnknownContentType()
    {
        var act = () => DiffBuilder.BuildUnifiedDiff("a", "b", contentType: "unknown");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildUnifiedDiffStatsCountInsertionsAndDeletions()
    {
        var inserted = DiffBuilder.BuildUnifiedDiff("line\n", "line\nextra\n");
        inserted.Stats.AddedLines.Should().Be(1);
        inserted.Stats.RemovedLines.Should().Be(0);

        var removed = DiffBuilder.BuildUnifiedDiff("one\n two\n three\n", "one\n three\n");
        removed.Stats.RemovedLines.Should().Be(1);
        removed.Stats.AddedLines.Should().Be(0);
    }

    [Fact]
    public void SummariseDiffResultsCombinesMultiple()
    {
        var first = DiffBuilder.BuildUnifiedDiff("alpha", "beta", fromLabel: "baseline", toLabel: "candidate");
        var second = DiffBuilder.BuildUnifiedDiff("line1\n", "line1\nline2\n", fromLabel: "left", toLabel: "right", contentType: "text");

        var summary = DiffBuilder.SummariseDiffResults(
            [first, second],
            versions: ["baseline", "candidate"],
            baselineNames: ["baseline.cfg", "left.cfg"],
            comparisonNames: ["candidate.cfg", "right.cfg"]);

        summary.ComparisonCount.Should().Be(2);
        summary.Comparisons[0].Metadata.BaselineName.Should().Be("baseline.cfg");
        summary.Comparisons[1].Summary.AddedLines.Should().Be(1);

        var payload = DiffBuilder.DiffSummaryToPayload(summary);
        payload["comparison_count"].Should().Be(2);
        var comparisons = DiffOracleData.List(payload["comparisons"]);
        DiffOracleData.Map(DiffOracleData.Map(comparisons[1])["metadata"])["comparison_name"].Should().Be("right.cfg");
    }

    [Fact]
    public void SummariseDiffResultsValidatesLengths()
    {
        var result = DiffBuilder.BuildUnifiedDiff("one", "two");

        var baseline = () => DiffBuilder.SummariseDiffResults([result], baselineNames: ["only", "extra"]);
        baseline.Should().Throw<ArgumentException>();

        var comparison = () => DiffBuilder.SummariseDiffResults([result], comparisonNames: ["only", "extra"]);
        comparison.Should().Throw<ArgumentException>();

        var empty = () => DiffBuilder.SummariseDiffResults([]);
        empty.Should().Throw<ArgumentException>();
    }
}
