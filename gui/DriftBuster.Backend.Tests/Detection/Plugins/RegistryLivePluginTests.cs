using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The registry-live plugin.</summary>
public sealed class RegistryLivePluginTests
{
    private static DetectionMatch? Detect(string name, string content)
        => new RegistryLivePlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private static List<string> Strings(object? value) => BinaryPluginTests.Strings(value);

    [Fact]
    public void RegistryLiveJsonAndYamlPaths()
    {
        const string jsonContent = """{"registry_scan": {"token": "App", "keywords": ["k"], "patterns": ["p"]}}""";
        var mJson = Detect("scan.json", jsonContent);
        mJson.Should().NotBeNull();
        mJson!.FormatName.Should().Be("registry-live");

        mJson.Variant.Should().Be("scan-definition");
        mJson.Confidence.Should().BeApproximately(0.8000000000000002, 1e-9);
        mJson.Reasons.Should().Equal(
            "Filename contains registry/scan hints",
            "Found 'registry_scan' top-level key in JSON payload",
            "Token provided: App",
            "Keyword list provided",
            "Pattern list provided");
        mJson.Metadata!.Keys.Should().Equal("token", "keywords", "patterns");
        mJson.Metadata["token"].Should().Be("App");
        Strings(mJson.Metadata["keywords"]).Should().Equal("k");
        Strings(mJson.Metadata["patterns"]).Should().Equal("p");

        const string yamlContent = "registry_scan:\n      token: App\n      keywords: [server, endpoint]\n      patterns:\n        - https://\n        - api.internal.local";
        var mYaml = Detect("scan.yaml", yamlContent);
        mYaml.Should().NotBeNull();
        mYaml!.FormatName.Should().Be("registry-live");

        mYaml.Variant.Should().Be("scan-definition");
        mYaml.Confidence.Should().BeApproximately(0.7, 1e-9);
        mYaml.Reasons.Should().Equal(
            "Filename contains registry/scan hints",
            "Detected 'registry_scan:' key in YAML content",
            "Inline keywords list present",
            "Pattern list present",
            "Token provided: App");
        mYaml.Metadata!.Keys.Should().Equal("token");
        mYaml.Metadata["token"].Should().Be("App");
    }

    [Fact]
    public void RegistryLiveInvalidJsonWithKeyReason()
    {
        const string bad = "{\"registry_scan\": {\"token\": \"App\""; // missing closing braces
        var m = Detect("scan.json", bad);
        // Detection may fall through to null; the code path must not throw.
        m.Should().BeNull();
    }

    [Fact]
    public void RegistryLiveFilenameHintAndOptions()
    {
        const string content = """{"registry_scan": {"token": "App", "max_depth": 5}}""";
        var m = Detect("myscan.regscan.json", content);
        m.Should().NotBeNull();
        // Filename hint reason and options captured
        m!.Reasons.Should().Contain(reason => reason.Contains("Filename suggests a registry scan JSON manifest", StringComparison.Ordinal));
        m.Metadata.Should().NotBeNull();
        m.Metadata!["max_depth"].Should().Be(5);

        m.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        m.Reasons.Should().Equal(
            "Filename suggests a registry scan JSON manifest",
            "Found 'registry_scan' top-level key in JSON payload",
            "Token provided: App");
        m.Metadata.Keys.Should().Equal("token", "max_depth");
    }

    [Fact]
    public void JsonKeyProbeIsLinearOnALineOfBraces()
    {
        var text = new string('{', 200_000) + "\"registry_scan\" : [";
        var started = System.Diagnostics.Stopwatch.StartNew();
        RegistryLivePlugin.HasJsonKey(text).Should().BeFalse();
        Detect("x.json", text).Should().BeNull();
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "registry_scan:\n  token: T\n  keywords: [a]\n  patterns:\n    - x\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.yaml", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        match!.Reasons.Should().Equal(
            "Detected 'registry_scan:' key in YAML content",
            "Inline keywords list present",
            "Pattern list present",
            "Token provided: T");
    }
}
