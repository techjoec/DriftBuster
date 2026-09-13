using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_registry_live_plugin.py; expected values were read from the Python plugin.</summary>
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
        mJson.Reasons.Should().Contain(reason => reason.Contains("Token provided", StringComparison.Ordinal));

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

        // Python's """...""".strip() keeps the literal's source indentation after the first line.
        const string yamlContent = "registry_scan:\n      token: App\n      keywords: [server, endpoint]\n      patterns:\n        - https://\n        - api.internal.local";
        var mYaml = Detect("scan.yaml", yamlContent);
        mYaml.Should().NotBeNull();
        mYaml!.FormatName.Should().Be("registry-live");
        mYaml.Reasons.Should().Contain(reason => reason.Contains("registry_scan:", StringComparison.Ordinal) || reason.Contains("Detected 'registry_scan:'", StringComparison.Ordinal));

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
        // Detection may fall through to None; ensure code path executes without error
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
    public void JsonPathWithoutSignalsUsesTheDefaultReasonAndNoMetadata()
    {
        var m = Detect("noext", """{"registry_scan": {}}""");
        m!.Confidence.Should().BeApproximately(0.65, 1e-9);
        m.Reasons.Should().Equal("Found 'registry_scan' top-level key in JSON payload");
        m.Metadata.Should().BeNull();

        // The key regex needs the brace on the same line as the key; the JSON extension still parses the document.
        var noRegexHit = Detect("x.json", "{\n\"registry_scan\": {}}");
        noRegexHit!.Reasons.Should().Equal("JSON manifest indicates registry live scan");
        noRegexHit.Metadata.Should().BeNull();

        var tokenOnly = Detect("x.json", "{\n\"registry_scan\": {\"token\":\"T\"}}");
        tokenOnly!.Reasons.Should().Equal("Token provided: T");
        tokenOnly.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);

        // A wrongly typed token and lists contribute nothing.
        var typed = Detect("x.json", """{"registry_scan": {"token": 5, "keywords": "no", "patterns": []}}""");
        typed!.Confidence.Should().BeApproximately(0.65, 1e-9);
        typed.Reasons.Should().Equal("Found 'registry_scan' top-level key in JSON payload");
        typed.Metadata.Should().BeNull();
    }

    [Fact]
    public void JsonValuesPassThroughAndListItemsAreStringified()
    {
        const string content = """{"registry_scan": {"max_depth": null, "max_hits": 1e3, "time_budget_s": 10.0, "keywords": [1, true, null, 1.5, [1,2], {"a": "b'c"}, "  "], "patterns": ["x", " "], "token": "  "}}""";
        var m = Detect("noext", content);
        m!.Confidence.Should().BeApproximately(0.7500000000000001, 1e-9);
        m.Reasons.Should().Equal(
            "Found 'registry_scan' top-level key in JSON payload",
            "Keyword list provided",
            "Pattern list provided");
        m.Metadata!.Keys.Should().Equal("keywords", "patterns", "max_depth", "max_hits", "time_budget_s");
        Strings(m.Metadata["keywords"]).Should().Equal("1", "True", "None", "1.5", "[1, 2]", "{'a': \"b'c\"}");
        Strings(m.Metadata["patterns"]).Should().Equal("x");
        m.Metadata["max_depth"].Should().BeNull();
        m.Metadata["max_hits"].Should().Be(1000.0);
        m.Metadata["time_budget_s"].Should().Be(10.0);

        const string numbers = """{"registry_scan": {"keywords": [1e400, -0, 12345678901234567890, 0.1, 1e16, 1e15, 1e-5, 1e-4, NaN, -Infinity, "\u00e9\n", "it's", "a\"b", "\u0007"]}}""";
        var stringified = Detect("x.json", numbers);
        Strings(stringified!.Metadata!["keywords"]).Should().Equal(
            "inf", "0", "12345678901234567890", "0.1", "1e+16", "1000000000000000.0", "1e-05", "0.0001", "nan", "-inf", "\u00e9\n", "it's", "a\"b", "\u0007");

        var nested = Detect("x.json", """{"registry_scan": {"keywords": [{"b": 1, "a": [null, false, 2.5]}]}}""");
        Strings(nested!.Metadata!["keywords"]).Should().Equal("{'b': 1, 'a': [None, False, 2.5]}");

        // Duplicate keys: the last value wins, as in a Python dict.
        var duplicates = Detect("x.json", """{"registry_scan": {"token": " T ", "token": "U"}, "registry_scan": {"token": "V"}}""");
        duplicates!.Metadata!["token"].Should().Be("V");
    }

    [Theory]
    [InlineData("a.txt", """{"registry_scan": {"token": "T"}}""", true)]
    [InlineData("a.txt", "{\n\"registry_scan\": {\"token\": \"T\"}}", false)]
    [InlineData("x.json", "\ufeff{\"registry_scan\": {\"token\":\"T\"}}", false)]
    [InlineData("x.json", "{\"registry_scan\": {\"token\":\"T\"}} ", true)]
    [InlineData("x.json", "{\"registry_scan\": {\"token\":\"T\"}}\x1c", false)]
    [InlineData("x.json", "[1]", false)]
    [InlineData("x.json", """{"registry_scan": 5}""", false)]
    [InlineData("x.yaml", "registry_scan: x\n", false)]
    public void JsonGateAndParseFailures(string name, string content, bool detected)
    {
        (Detect(name, content) is not null).Should().Be(detected);
    }

    [Fact]
    public void JsonKeyReasonCarriesIntoTheYamlPath()
    {
        var m = Detect("reg.yaml", "{\"registry_scan\": {\"token\": \"T\"}} trailing\nregistry_scan:\ntoken: \"Q\"\nkeywords: [a]\npatterns: -");
        m!.Confidence.Should().BeApproximately(0.7, 1e-9);
        m.Reasons.Should().Equal(
            "Filename contains registry/scan hints",
            "Found 'registry_scan' top-level key in JSON payload",
            "Detected 'registry_scan:' key in YAML content",
            "Inline keywords list present",
            "Pattern list present",
            "Token provided: Q");
        m.Metadata!["token"].Should().Be("Q");
    }

    [Fact]
    public void YamlKeyIsCaseInsensitiveAndABlankTokenIsDropped()
    {
        var m = Detect("Scan.YML", "REGISTRY_SCAN :\n  token:   \n");
        m!.Confidence.Should().BeApproximately(0.62, 1e-9);
        m.Reasons.Should().Equal("Filename contains registry/scan hints", "Detected 'registry_scan:' key in YAML content");
        m.Metadata.Should().BeNull();

        // Python IGNORECASE: dotless i matches i.
        var dotless = Detect("x.yaml", "reg\u0131stry_scan:\ntoken: a");
        dotless!.Reasons.Should().Equal("Detected 'registry_scan:' key in YAML content", "Token provided: a");
        dotless.Confidence.Should().BeApproximately(0.7, 1e-9);

        // U+001C is whitespace to Python's \s.
        var separators = Detect("x.yaml", "\x1cregistry_scan:\x1c\ntoken:\x1c'q'\x1c\n");
        separators!.Metadata!["token"].Should().Be("q");
    }

    [Fact]
    public void YamlTokenMayFollowOnTheNextLineAndListsNeedTheirClosers()
    {
        var m = Detect("x.yaml", "registry_scan:\n\n\ntoken:\n  'val'\n  keywords: [a\n  patterns: [");
        m!.Confidence.Should().BeApproximately(0.7, 1e-9);
        m.Reasons.Should().Equal(
            "Detected 'registry_scan:' key in YAML content",
            "Pattern list present",
            "Token provided: val");
        m.Metadata!.Keys.Should().Equal("token");
        m.Metadata["token"].Should().Be("val");
    }

    [Theory]
    [InlineData("{\"registry_scan\": {", true)]
    [InlineData("{ \"x\": 1, \"registry_scan\"\n\t:\n{", true)]
    [InlineData("{\n\"registry_scan\": {", false)]
    [InlineData("\"registry_scan\": {", false)]
    [InlineData("{\"registry_scan\": [", false)]
    [InlineData("{\"registry_scan\":", false)]
    [InlineData("{\r\"registry_scan\": {", false)]
    [InlineData("{\"registry_scan\"\x1c:\x1c{", true)]
    public void JsonKeyProbeMatchesThePythonPattern(string text, bool expected)
    {
        RegistryLivePlugin.HasJsonKey(text).Should().Be(expected);
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

    [Fact]
    public void DeepListsParseUpToTheDecoderLimitAndAreStringifiedWithoutRecursion()
    {
        // Python: two outer objects plus the keywords list leave 9995 levels for the item; one more raises
        // RecursionError inside json.loads, which the plugin catches, so nothing is detected.
        static string Keywords(int inner) => "{\"registry_scan\": {\"token\": \"T\", \"keywords\": [" + new string('[', inner) + new string(']', inner) + "]}}";

        var match = StackProbe.RunOnSmallStack(() => Detect("kw.txt", Keywords(9995)));
        match!.Confidence.Should().BeApproximately(0.75, 1e-9);
        match.Reasons.Should().Equal("Found 'registry_scan' top-level key in JSON payload", "Token provided: T", "Keyword list provided");
        Strings(match.Metadata!["keywords"]).Should().Equal(new string('[', 9995) + new string(']', 9995));

        Detect("kw.txt", Keywords(9996)).Should().BeNull();
        Detect("kw.txt", Keywords(60000)).Should().BeNull();
    }

    [Fact]
    public void DeepOptionValuesPassThroughAndValidateWithoutRecursion()
    {
        var content = "{\"registry_scan\": {\"token\": \"T\", \"max_depth\": " + new string('[', 9996) + new string(']', 9996) + "}}";

        var metadata = StackProbe.RunOnSmallStack(() =>
        {
            var match = Detect("md.json", content)!;
            match.Metadata!.Keys.Should().Equal("token", "max_depth");
            return DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
        });

        metadata["max_depth"].Should().BeOfType<List<object?>>();
        metadata["catalog_format"].Should().Be("registry-live");
        Detect("md.json", "{\"registry_scan\": {\"token\": \"T\", \"max_depth\": " + new string('[', 9997) + new string(']', 9997) + "}}").Should().BeNull();
    }

    [Theory]
    [InlineData(50000, "\n")]
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
