using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_toml_flags.py; expected values were read from the Python plugin.</summary>
public sealed class TomlFlagsTests
{
    private static DetectionMatch? Detect(string name, string content) => TomlPluginTests.Detect(name, content);

    [Fact]
    public void TomlTrailingCommaFlag()
    {
        var content = """

                [server]
                ports = [8000,]
                
            """.Trim();
        var match = Detect("config.toml", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = YamlPluginTests.Strings(match.Metadata["review_reasons"]);
        reviewReasons.Should().Contain(reason => reason.Contains("trailing comma", StringComparison.Ordinal));

        reviewReasons.Should().Equal("Array with trailing comma before closing bracket");
        match.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [table] headers typical of TOML",
            "Detected key = value assignments",
            "Found array value assignments");
        match.Metadata.Keys.Should().Equal("key_value_spacing", "needs_review", "review_reasons");
        TomlPluginTests.AssertOneSpaceProfile(match);
    }

    [Fact]
    public void TomlBareKeysFlag()
    {
        var content = """

                title = "Example"
                key1
                key2
                key3
                
            """.Trim();
        var match = Detect("plain.toml", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = YamlPluginTests.Strings(match.Metadata["review_reasons"]);
        reviewReasons.Should().Contain(reason => reason.Contains("bare key", StringComparison.Ordinal));

        reviewReasons.Should().Equal("Multiple bare key lines without '=' suggest malformed TOML");
        match.Confidence.Should().BeApproximately(0.7200000000000001, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Detected key = value assignments",
            "Found quoted value assignments");

        // Table headers switch the bare-key heuristic off.
        var headed = Detect("bare.toml", "[t]\nk1\nk2\nk3\nx = 'y'\n");
        headed!.Metadata!.Keys.Should().Equal("key_value_spacing");
    }

    [Fact]
    public void TomlTabSpacingTriggersReview()
    {
        var content = """

                [tool.sample]
                name	=	"demo"
                other = 1
                
            """.Trim();
        var match = Detect("spacing.toml", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = YamlPluginTests.Strings(match.Metadata["review_reasons"]);
        reviewReasons.Should().Contain(reason => reason.Contains("Tab characters around '='", StringComparison.Ordinal));

        reviewReasons.Should().Equal("Tab characters around '=' detected in TOML sample");
        match.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
        var spacing = TomlPluginTests.Spacing(match);
        spacing.Keys.Should().Equal("before", "allowed_before", "after", "allowed_after", "tab_lines");
        // Two assignment lines tie at 0 and 1 spaces; Counter.most_common keeps the first seen (the tab line, 0).
        spacing["before"].Should().Be(0);
        YamlPluginTests.Ints(spacing["allowed_before"]).Should().Equal(0, 1);
        spacing["after"].Should().Be(0);
        YamlPluginTests.Ints(spacing["allowed_after"]).Should().Equal(0, 1);
        YamlPluginTests.Ints(spacing["tab_lines"]).Should().Equal(2);
    }
}
