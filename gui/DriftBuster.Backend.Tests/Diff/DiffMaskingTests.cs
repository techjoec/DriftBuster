using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Mirror of tests/reporting/test_diff_masking.py.</summary>
[Collection(DiffSafetyLimitsCollection.Name)]
public sealed class DiffMaskingTests
{
    [Fact]
    public void BuildUnifiedDiffRecordsCustomRedactorTokensAndCounts()
    {
        var redactor = new RedactionFilter(["secret", "api-key"], placeholder: "<mask>");

        var result = DiffBuilder.BuildUnifiedDiff(
            "secret token\n",
            "secret token\napi-key=VALUE\n",
            redactor: redactor);

        result.Placeholder.Should().Be("<mask>");
        result.MaskTokens.Should().Equal("api-key", "secret");
        result.RedactionCounts.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal) { ["secret"] = 2, ["api-key"] = 1 });
        result.Diff.Should().Contain("<mask> token");
        result.Diff.Should().NotContain("api-key");
        result.Diff.Should().NotContain("secret");
    }

    [Fact]
    public void BuildUnifiedDiffWithoutTokensReturnsEmptyRedactionCounts()
    {
        var result = DiffBuilder.BuildUnifiedDiff("line", "line");

        result.RedactionCounts.Should().BeNull();
        result.MaskTokens.Should().BeNull();
    }
}
