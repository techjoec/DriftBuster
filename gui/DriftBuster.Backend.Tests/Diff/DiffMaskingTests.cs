using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Token masking in diffs.</summary>
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
}
