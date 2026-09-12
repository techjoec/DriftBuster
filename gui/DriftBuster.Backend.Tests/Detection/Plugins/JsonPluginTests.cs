using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_json_plugin.py, plus scanner checks that pin the Python json.loads grammar.</summary>
public sealed class JsonPluginTests
{
    private static DetectionMatch? Detect(JsonPlugin plugin, string filename, string payload)
        => plugin.Detect(filename, Encoding.UTF8.GetBytes(payload), payload);

    private static DetectionMatch? Detect(string filename, string payload) => Detect(new JsonPlugin(), filename, payload);

    [Fact]
    public void JsonPluginDetectsStructuredSettings()
    {
        var plugin = new JsonPlugin();
        var match = Detect(
            plugin,
            "appsettings.json",
            """{"Logging": {"LogLevel": {"Default": "Information"}}, "ConnectionStrings": {"Default": "sqlite"}}""");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("json");
        match.Variant.Should().Be("structured-settings-json");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["settings_hint"].Should().Be("filename");
        match.Metadata.Should().NotContainKey("settings_environment");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .json suggests JSON content",
            "Detected JSON object opening token '{'",
            "Found key/value signature indicative of JSON objects",
            "Curly/array delimiters appear balanced in sampled content",
            "Matched appsettings-style configuration cues",
            "Parsed JSON payload without errors within sample");
        match.Metadata.Keys.Should().Equal("top_level_type", "settings_hint", "top_level_keys");
        match.Metadata["top_level_keys"].Should().BeEquivalentTo(new[] { "Logging", "ConnectionStrings" }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void JsonPluginDetectsStructuredSettingsEnvironment()
    {
        var match = Detect("appsettings.Staging.json", """{"Logging": {"LogLevel": {"Default": "Warning"}}}""");

        match.Should().NotBeNull();
        match!.Variant.Should().Be("structured-settings-json");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["settings_environment"].Should().Be("staging");
    }

    [Fact]
    public void JsonPluginDetectsJsonWithComments()
    {
        var match = Detect("config.jsonc", "// comment\n{\"key\": 1, /* multi */ \"enabled\": true}");

        match.Should().NotBeNull();
        match!.Variant.Should().Be("jsonc");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["has_comments"].Should().Be(true);
        match.Metadata["parsed_with_comment_stripping"].Should().Be(true);
        match.Metadata.Should().ContainKey("top_level_keys");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .jsonc suggests JSON content",
            "Detected JSON object opening token '{'",
            "Detected comment tokens outside string literals",
            "Found key/value signature indicative of JSON objects",
            "Curly/array delimiters appear balanced in sampled content",
            "Parsed JSON payload without errors within sample");
        match.Metadata["top_level_keys"].Should().BeEquivalentTo(new[] { "key", "enabled" }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void JsonPluginRejectsNonJsonPayloads()
    {
        Detect("notes.txt", "Just some text without braces").Should().BeNull();
    }

    [Fact]
    public void JsonPluginHandlesCommentOnlyPayload()
    {
        Detect("config.json", "// comment only\n/* block */").Should().BeNull();
    }

    [Fact]
    public void JsonPluginDetectsGenericWithoutExtension()
    {
        var match = Detect("config.settings", """{"key": 1, "list": [1, 2, 3]}""");
        match.Should().NotBeNull();
        match!.Variant.Should().Be("generic");
        match.Metadata.Should().NotBeNull();
        match.Metadata.Should().ContainKey("top_level_keys");
        match.Confidence.Should().BeApproximately(0.9000000000000001, 1e-9);
        match.Reasons.Should().Equal(
            "Detected JSON object opening token '{'",
            "Found key/value signature indicative of JSON objects",
            "Curly/array delimiters appear balanced in sampled content",
            "Parsed JSON payload without errors within sample");
    }

    [Fact]
    public void JsonPluginAttemptParseArrayMetadata()
    {
        var match = Detect("data.json", "[{\"value\": 1}, {\"value\": 2}]");
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata.Should().ContainKey("top_level_sample_types");
        match.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(new[] { "dict" });
        match.Metadata["top_level_type"].Should().Be("array");
    }

    [Fact]
    public void JsonPluginRequiresSignalsForCustomExtension()
    {
        Detect("config.settings", "[1, 2").Should().BeNull();
    }

    [Fact]
    public void JsonPluginDetectReturnsNoneForMissingText()
    {
        new JsonPlugin().Detect("config.json", "{}"u8.ToArray(), null).Should().BeNull();
    }

    [Fact]
    public void JsonPluginCommentHelpers()
    {
        var (stripped, consumed) = JsonPlugin.StripLeadingComments("/*unterminated");
        stripped.Should().BeEmpty();
        consumed.Should().BeTrue();

        (stripped, consumed) = JsonPlugin.StripLeadingComments("// no newline");
        stripped.Should().BeEmpty();
        consumed.Should().BeTrue();

        JsonPlugin.ContainsComments("""{"value": "// comment"}""").Should().BeFalse();
        JsonPlugin.ContainsComments("""{"value": true}// trailing""").Should().BeTrue();
        JsonPlugin.ContainsComments("""{"path": "C\\Temp"}""").Should().BeFalse();

        JsonPlugin.HasKeyValueMarker("""{"text": "escaped "quote""}""").Should().BeTrue();
        JsonPlugin.HasKeyValueMarker("""{"items": [1, 2]}""").Should().BeTrue();

        var truncated = JsonPlugin.TruncateToStructuralBoundary("""{"path": "C\\Temp"} // trailing""");
        truncated.Should().StartWith("""{"path": "C\\Temp"}""");

        JsonPlugin.IsStructuredSettings("config.json", "\"ConnectionStrings\": {}").Should().Be("content");
        var parse = JsonPlugin.AttemptParse("""{"unterminated": }""", allowComments: false);
        parse.Success.Should().BeFalse();

        var (cleaned, removed) = JsonPlugin.StripJsonComments(
            """
            {
                    "key": "// inside string",
                    // comment
                    "flag": true
                }
            """);
        removed.Should().BeTrue();
        cleaned.Should().Contain("// inside string");
    }

    [Fact]
    public void JsonPluginUnknownTopLevelWithExtension()
    {
        var match = Detect("sample.json", "\"text\"");
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["top_level_type"].Should().Be("unknown");
        // The structural-boundary cut never advances inside a string literal, so a bare string leaves an empty
        // snippet: the parse fails and, with balanced (zero) delimiters, the review flag is raised.
        match.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        match.Metadata["parse_failed"].Should().Be(true);
        match.Metadata["needs_review"].Should().Be(true);
    }

    [Fact]
    public void JsonPluginSignalsGuardForCustomExtension()
    {
        Detect("data.custom", "{{").Should().BeNull();
    }

    [Fact]
    public void JsonPluginLargePayloadGetsClamped()
    {
        var payload = "{\"key\": \"" + new string('a', 250_000) + "\"}";
        var match = Detect("massive.json", payload);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["analysis_window_truncated"].Should().Be(true);
        match.Metadata["analysis_window_chars"].Should().Be(200_000);
        match.Confidence.Should().BeApproximately(0.8500000000000001, 1e-9);
        match.Metadata.Should().NotContainKey("parse_failed");
    }

    [Fact]
    public void JsonPluginCommentStrippingPreservesArrays()
    {
        // The Python literal ends in "}\n" followed by the four spaces of its closing indentation.
        const string content = """
            {
                    // comment about endpoints
                    "endpoints": [
                        "https://example.local",
                        "https://api.local" // trailing comment
                    ]
                }
            """ + "\n    ";
        var match = Detect("config.jsonc", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["parsed_with_comment_stripping"].Should().Be(true);
        match.Metadata["top_level_keys"].Should().BeEquivalentTo(new[] { "endpoints" });
    }

    // The scanner must accept and reject exactly what CPython's json.loads does (values verified with the interpreter).

    [Fact]
    public void ScannerAcceptsPythonOnlyLiterals()
    {
        var match = Detect("nan.json", "[NaN, Infinity, -Infinity]");
        match!.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(new[] { "float" });
        match.Metadata.Should().NotContainKey("parse_failed");

        var scalar = Detect("neg-inf-top.json", "-Infinity");
        scalar!.Metadata!["top_level_type"].Should().Be("unknown");
        scalar.Metadata.Should().NotContainKey("parse_failed");
    }

    [Fact]
    public void ScannerReportsPythonTypeNames()
    {
        var match = Detect("mix.json", "[1, 1.5, \"s\", true, null, {}, [], -0, 1e5]");
        match!.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(
            new[] { "NoneType", "bool", "float", "int", "str" },
            options => options.WithStrictOrdering());

        Detect("num5.json", "[1.5e+3, 2E-1, -0.0]")!.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(new[] { "float" });
    }

    [Fact]
    public void ScannerKeepsFirstOccurrenceOrderForDuplicateKeys()
    {
        var match = Detect("dupe.json", """{"b": 1, "a": 2, "b": 3, "c": 4, "d": 5, "e": 6, "f": 7}""");
        match!.Metadata!["top_level_keys"].Should().BeEquivalentTo(new[] { "b", "a", "c", "d", "e" }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ScannerHandlesSurrogateEscapesLikePython()
    {
        Detect("lone.json", """{"k": "\ud83d"}""")!.Metadata.Should().NotContainKey("parse_failed");
        var pair = Detect("pair.json", "{\"k\": \"\\ud83d\\ude00\", \"j\": \"\\ud83d\\u0041\"}");
        pair!.Metadata!["top_level_keys"].Should().BeEquivalentTo(new[] { "k", "j" }, options => options.WithStrictOrdering());
        Detect("bad-u.json", """{"a": "\u12G4"}""")!.Metadata!["parse_failed"].Should().Be(true);
        Detect("bad-esc.json", """{"a": "\x"}""")!.Metadata!["parse_failed"].Should().Be(true);
    }

    [Fact]
    public void ScannerRejectsWhatPythonRejects()
    {
        foreach (var payload in new[] { "[1.]", "[1e, 2]", "[-]", "[01]", "[nul]", "{1: 2}", "{\"a\" 1}", "{\"a\": 1, }", "[1, ]", "{\"a\": 1} x", "{\"k\": \"a\tb\"}", "{\"a\": \"\x1f\"}" })
        {
            var match = Detect("case.json", payload);
            match.Should().NotBeNull(payload);
            match!.Metadata!["parse_failed"].Should().Be(true, payload);
        }

        Detect("del-char.json", "{\"a\": \"\x7f\"}")!.Metadata.Should().NotContainKey("parse_failed");
        Detect("ff-ws.json", "\f{\"a\": 1}")!.Metadata.Should().NotContainKey("parse_failed");
    }

    [Fact]
    public void ScannerRejectsBomAfterCommentsButStripsLeadingOnes()
    {
        var bom = Detect("bom.json", "// c\n\uFEFF{}");
        bom!.Metadata!["top_level_type"].Should().Be("unknown");
        bom.Metadata.Should().NotContainKey("parse_failed");
        bom.Confidence.Should().BeApproximately(0.7300000000000001, 1e-9);

        Detect("BOMfile.json", "\uFEFF\uFEFF{\"a\": 1}")!.Metadata!["top_level_keys"].Should().BeEquivalentTo(new[] { "a" });
    }

    // CPython 3.13 raises RecursionError decoding 20000 nested arrays (5000 still parse); the explicit-stack
    // scanner has no depth limit, and it never converts integers, so the int() digit limit does not apply either.
    [Fact]
    public void ScannerHasNoNestingOrDigitLimit()
    {
        var nested = Detect("nested.json", new string('[', 20000) + new string(']', 20000));
        nested!.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(new[] { "list" });
        nested.Confidence.Should().BeApproximately(0.95, 1e-9);

        var digits = Detect("big-int.json", "[" + new string('1', 5000) + "]");
        digits!.Metadata!["top_level_sample_types"].Should().BeEquivalentTo(new[] { "int" });
        digits.Metadata.Should().NotContainKey("parse_failed");
    }

    [Fact]
    public void WindowsAreMeasuredInCodePoints()
    {
        var payload = "{\"key\": \"" + string.Concat(Enumerable.Repeat("\U0001F600", 199_990)) + "\"}";
        var match = Detect("astral.json", payload);
        match!.Metadata!["analysis_window_truncated"].Should().Be(true);
        match.Metadata["analysis_window_chars"].Should().Be(200_000);
    }

    [Fact]
    public void AppsettingsEnvironmentJoinsInnerSegments()
    {
        var match = Detect("appsettings.Dev.Local.json", "{}");
        match!.Metadata!["settings_environment"].Should().Be("dev.local");
        match.Metadata["top_level_keys"].Should().BeEquivalentTo(Array.Empty<string>());

        var bare = Detect(".json", "{}");
        bare!.Reasons.Should().Contain("File extension .json suggests JSON content");
    }

    // No ^-anchored pattern here, but the same whitespace runs must stay cheap on the scanner and the structure pass.
    [Theory]
    [InlineData(50000, "\n")]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "{\"a\": 1}\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.json", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Metadata!["top_level_keys"].Should().BeAssignableTo<IEnumerable<string>>().Subject.Should().Equal("a");
        match.Metadata["top_level_type"].Should().Be("object");
    }
}
