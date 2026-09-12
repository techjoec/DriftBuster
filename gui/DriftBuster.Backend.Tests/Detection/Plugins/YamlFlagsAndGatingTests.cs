using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_yaml_flags_and_gating.py; expected values were read from the Python plugin.</summary>
public sealed class YamlFlagsAndGatingTests
{
    private static DetectionMatch? Detect(string name, string content) => YamlPluginTests.Detect(name, content);

    [Fact]
    public void YamlTabsFlagNeedsReview()
    {
        var match = Detect("config.yaml", "apiVersion: v1\n\tkind: ConfigMap\nmetadata:\n  name: app\n");
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = YamlPluginTests.Strings(match.Metadata["review_reasons"]);
        reviewReasons.Should().Contain(reason => reason.Contains("Tab indentation", StringComparison.Ordinal));

        reviewReasons.Should().Equal("Tab indentation present in YAML-like content");
        match.Variant.Should().Be("kubernetes-manifest");
        match.Confidence.Should().BeApproximately(0.9, 1e-9);
        var indentation = YamlPluginTests.Indentation(match);
        indentation.Keys.Should().Equal("style", "baseline", "allowed_widths", "tab_lines");
        indentation["style"].Should().Be("mixed");
        indentation["baseline"].Should().Be(2);
        YamlPluginTests.Ints(indentation["tab_lines"]).Should().Equal(2);
        YamlPluginTests.Strings(match.Metadata["top_level_keys_preview"]).Should().Equal("apiVersion", "kind", "metadata");
    }

    [Fact]
    public void YamlDocMarkerReason()
    {
        var match = Detect("simple.yaml", "---\nkey: value\n");
        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("document start", StringComparison.OrdinalIgnoreCase));
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .yaml suggests YAML content",
            "Detected YAML document start marker '---'",
            "Found key: value pairs indicative of YAML");
        match.Metadata!.Keys.Should().Equal("top_level_keys_preview");
    }

    [Fact]
    public void YamlIniLikeExtensionRequiresStructure()
    {
        // .conf with weak structure should be rejected
        Detect("mongod.conf", "key: value\n").Should().BeNull();

        // But a .conf with stronger YAML structure is accepted
        var strong = Detect("service.conf", "---\nservers:\n  item: true\n  nested:\n    key: value\n  - list: yes\n");
        strong.Should().NotBeNull();
        strong!.Confidence.Should().BeApproximately(0.8, 1e-9);
        strong.Reasons.Should().Equal(
            "Detected YAML document start marker '---'",
            "Detected YAML list marker '- '",
            "Found key: value pairs indicative of YAML",
            "Found indented nested key: value blocks");
        YamlPluginTests.Ints(YamlPluginTests.Indentation(strong)["allowed_widths"]).Should().Equal(2, 4);
        YamlPluginTests.Strings(strong.Metadata!["top_level_keys_preview"]).Should().Equal("servers", "nested");
    }

    [Fact]
    public void YamlInconsistentIndentationTriggersReview()
    {
        var match = Detect("indent.yaml", "root:\n  good: value\n     bad: indent\n");
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reasons = YamlPluginTests.Strings(match.Metadata["review_reasons"]);
        reasons.Should().Contain(reason => reason.Contains("Indentation widths outside tolerated range", StringComparison.Ordinal));

        reasons.Should().Equal("Indentation widths outside tolerated range detected");
        match.Confidence.Should().BeApproximately(0.85, 1e-9);
        var indentation = YamlPluginTests.Indentation(match);
        indentation.Keys.Should().Equal("style", "baseline", "allowed_widths", "outlier_lines");
        YamlPluginTests.Ints(indentation["outlier_lines"]).Should().Equal(3);
        YamlPluginTests.Strings(match.Metadata["top_level_keys_preview"]).Should().Equal("root", "bad");
    }
}
