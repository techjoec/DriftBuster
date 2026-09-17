using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>Mirror of tests/reporting/test_summary.py.</summary>
public sealed class SummaryTests
{
    private static DetectionMatch BuildMatch(string formatName, string? variant, double confidence = 0.8, string? pluginName = null)
    {
        var match = new DetectionMatch(pluginName ?? formatName, formatName, variant, confidence, ["synthetic"]);
        match.Metadata = DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
        return match;
    }

    private static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    private static List<OrderedDictionary<string, object?>> Maps(object? value)
        => ((List<object?>)value!).Cast<OrderedDictionary<string, object?>>().ToList();

    private static HashSet<string> Strings(object? value) => ((List<object?>)value!).Cast<string>().ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void SummaryCapturesVariantMetadataAndRemediations()
    {
        var dotenv = BuildMatch("ini", "dotenv");
        var unixConf = BuildMatch("unix-conf", "generic-directive-text", confidence: 0.6, pluginName: "conf");

        var summary = DetectionSummary.Summarise([dotenv, unixConf]);

        summary["total_matches"].Should().Be(2);
        summary["unique_formats"].Should().Be(2);
        Map(summary["severity_counts"]).Should().Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["high"] = 1, ["medium"] = 1 });

        var formats = Maps(summary["formats"]).ToDictionary(entry => (string)entry["format"]!, StringComparer.Ordinal);
        var iniSummary = formats["ini"];
        var dotenvVariant = Maps(iniSummary["variants"])[0];

        dotenvVariant["severity"].Should().Be("high");
        Strings(dotenvVariant["metadata_keys"]).Should().Contain("catalog_severity");
        var remediationIds = Strings(dotenvVariant["remediation_ids"]);
        remediationIds.IsSupersetOf(["ini-dotenv-rotate-secrets", "ini-secret-rotation"]).Should().BeTrue();

        var remediationIndex = Maps(summary["remediations"]).Select(entry => (string)entry["id"]!).ToHashSet(StringComparer.Ordinal);
        remediationIndex.IsSupersetOf(["ini-secret-rotation", "ini-dotenv-rotate-secrets"]).Should().BeTrue();

        var dotenvRemediation = Maps(summary["remediations"]).ToDictionary(entry => (string)entry["id"]!, StringComparer.Ordinal)["ini-dotenv-rotate-secrets"];
        ((List<object?>)dotenvRemediation["formats"]!).Should().Equal("ini");
        ((List<object?>)dotenvRemediation["variants"]!).Should().Equal("dotenv");
    }

    [Fact]
    public void SummaryHandlesMatchesWithoutMetadata()
    {
        var plainMatch = new DetectionMatch("text", "text", null, 0.4, []);

        var summary = DetectionSummary.Summarise([plainMatch]);

        summary["total_matches"].Should().Be(1);
        var textSummary = Maps(summary["formats"])[0];
        var variantEntry = Maps(textSummary["variants"])[0];

        variantEntry["variant"].Should().BeNull();
        ((List<object?>)variantEntry["remediation_ids"]!).Should().BeEmpty();
        ((List<object?>)summary["remediations"]!).Should().BeEmpty();
        Map(summary["severity_counts"]).Should().BeEmpty();
    }
}
