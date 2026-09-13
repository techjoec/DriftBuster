using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Mirror of tests/reporting/test_diff_builder.py.</summary>
[Collection(DiffSafetyLimitsCollection.Name)]
public sealed class DiffBuilderTests
{
    [Fact]
    public void CanonicaliseTextNormalisesNewlines()
    {
        var payload = "line1\r\nline2 \r\n";
        var normalised = Canonicaliser.CanonicaliseText(payload);
        TextLines.SplitLines(normalised).Should().Equal("line1", "line2");
    }

    [Fact]
    public void CanonicaliseTextRemovesBomAndUnicodeNewlines()
    {
        var payload = "\uFEFFalpha\u2028beta\u2029gamma\u0085";
        var normalised = Canonicaliser.CanonicaliseText(payload);
        normalised.Should().EndWith("\n");
        TextLines.SplitLines(normalised).Should().Equal("alpha", "beta", "gamma");
    }

    [Fact]
    public void CanonicaliseJsonSortsKeysWithStableIndent()
    {
        var payload = """{"b": 1, "a": {"z": 2, "y": 1}}""";
        var canonical = Canonicaliser.CanonicaliseJson(payload);
        canonical.Should().Be("{\n  \"a\": {\n    \"y\": 1,\n    \"z\": 2\n  },\n  \"b\": 1\n}");
    }

    [Fact]
    public void CanonicaliseJsonFallsBackOnInvalidPayload()
    {
        var malformed = "{not-json";
        Canonicaliser.CanonicaliseJson(malformed).Should().Be(Canonicaliser.CanonicaliseText(malformed));
    }

    [Fact]
    public void CanonicaliseXmlPreservesPrologAndHandlesDoctype()
    {
        var payload =
            "<?xml version=\"1.0\"?>\n"
            + "<!DOCTYPE note [<!ELEMENT note (to,from,heading,body)>]>\n"
            + "<note attr=' value '>\n  <to>SECRET</to>\n</note>";
        var canonical = Canonicaliser.CanonicaliseXml(payload);
        canonical.Should().StartWith("<?xml");
        canonical.Should().Contain("<!DOCTYPE");
        canonical.Should().Contain("SECRET");
    }

    [Fact]
    public void BuildUnifiedDiffAppliesRedaction()
    {
        var result = DiffBuilder.BuildUnifiedDiff(
            "token = SECRET\n",
            "token = SECRET\nextra = 1\n",
            maskTokens: ["SECRET"]);
        result.Should().BeOfType<DiffArtifact>();
        result.Diff.Should().Contain("[REDACTED]");
        result.Stats.AddedLines.Should().Be(1);
        result.MaskTokens.Should().Equal("SECRET");
        result.RedactionCounts.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal) { ["SECRET"] = 2 });
    }

    [Fact]
    public void BuildUnifiedDiffUsesJsonCanonicalisation()
    {
        var before = """{"b": 1, "a": 2}""";
        var after = """{"b": 2, "a": 2}""";
        var result = DiffBuilder.BuildUnifiedDiff(before, after, contentType: "json");

        result.ContentType.Should().Be("json");
        result.CanonicalBefore.Should().StartWith("{\n  \"a\"");
        result.CanonicalBefore.Should().Contain("  \"b\": 1");
        result.CanonicalAfter.Should().Contain("  \"b\": 2");
        result.Diff.Should().Contain("-  \"b\": 1");
        result.Diff.Should().Contain("+  \"b\": 2");
    }

    [Fact]
    public void RenderUnifiedDiffAndErrors()
    {
        var diffText = DiffBuilder.RenderUnifiedDiff("a", "b", contentType: "text");
        diffText.Should().Contain("-a").And.Contain("+b");

        var act = () => DiffBuilder.BuildUnifiedDiff("a", "b", contentType: "unknown");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildBinaryDiffTracksEvidence()
    {
        byte[] before = [0x00, 0x01];
        byte[] after = [0x00, 0x01, 0x02];
        var result = DiffBuilder.BuildBinaryDiff(before, after, label: "sqlite");

        result.ContentType.Should().Be("binary");
        result.Diff.Should().Contain("binary:sqlite");
        result.BinaryEvidence.Should().NotBeNull();
        var evidence = result.BinaryEvidence![0];
        evidence.Changed.Should().BeTrue();
        evidence.BeforeSize.Should().Be(2);
        evidence.AfterSize.Should().Be(3);
    }

    [Fact]
    public void BuildUnifiedDiffTruncatesLargePayloads()
    {
        var saved = (DiffSafetyLimits.MaxCanonicalBytes, DiffSafetyLimits.MaxDiffBytes, DiffSafetyLimits.MaxDiffLines);
        try
        {
            DiffSafetyLimits.MaxCanonicalBytes = 32;
            DiffSafetyLimits.MaxDiffBytes = 48;
            DiffSafetyLimits.MaxDiffLines = 2;

            var before = "alpha\n" + new string('x', 64);
            var after = "beta\n" + new string('y', 64);

            var result = DiffBuilder.BuildUnifiedDiff(before, after, contentType: "text");

            result.SafetyLimits.Should().NotBeNull();
            var safety = result.SafetyLimits!;
            var canonicalLimits = safety.Canonical;
            canonicalLimits.Should().NotBeNull();
            canonicalLimits!.Before.Should().NotBeNull();
            canonicalLimits.After.Should().NotBeNull();
            canonicalLimits.Before!.TruncatedBytes.Should().BeGreaterThan(0);
            canonicalLimits.After!.TruncatedBytes.Should().BeGreaterThan(0);
            result.CanonicalBefore.Should().Contain("truncated");
            result.CanonicalAfter.Should().Contain("digest=");

            var diffLimits = safety.Diff;
            diffLimits.Should().NotBeNull();
            diffLimits!.TruncatedLines.Should().BeGreaterThanOrEqualTo(0);
            diffLimits.TruncatedBytes.Should().BeGreaterThanOrEqualTo(0);
            result.Diff.Should().Contain("diff truncated");
        }
        finally
        {
            (DiffSafetyLimits.MaxCanonicalBytes, DiffSafetyLimits.MaxDiffBytes, DiffSafetyLimits.MaxDiffLines) = saved;
        }
    }
}
