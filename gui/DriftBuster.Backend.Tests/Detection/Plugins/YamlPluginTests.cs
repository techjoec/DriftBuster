using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_yaml_plugin.py; expected values were read from the Python plugin.</summary>
public sealed class YamlPluginTests
{
    internal static DetectionMatch? Detect(string name, string content)
        => new YamlPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    internal static OrderedDictionary<string, object?> Indentation(DetectionMatch match)
        => match.Metadata!["indentation"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    internal static IEnumerable<int> Ints(object? value) => value.Should().BeAssignableTo<IEnumerable<int>>().Subject;

    internal static IEnumerable<string> Strings(object? value) => value.Should().BeAssignableTo<IEnumerable<string>>().Subject;

    [Fact]
    public void YamlGenericDetection()
    {
        const string content = """

                title: Example
                enabled: true
                servers:
                  - host1
                  - host2
                nested:
                  key: value
                
            """;
        var match = Detect("settings.yaml", content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("yaml");
        match.Variant.Should().Be("generic");
        match.Reasons.Should().Contain(reason => reason.Contains("key: value", StringComparison.Ordinal) || reason.Contains("Found key:", StringComparison.Ordinal));
        match.Confidence.Should().BeApproximately(0.9, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .yaml suggests YAML content",
            "Detected YAML list marker '- '",
            "Found key: value pairs indicative of YAML",
            "Found indented nested key: value blocks");
        match.Metadata!.Keys.Should().Equal("indentation", "top_level_keys_preview");
        var indentation = Indentation(match);
        indentation.Keys.Should().Equal("style", "baseline", "allowed_widths");
        indentation["style"].Should().Be("spaces");
        indentation["baseline"].Should().Be(4);
        Ints(indentation["allowed_widths"]).Should().Equal(4, 6);
        // "servers:" swallows the next line's first character, so "- host1" cannot anchor and "key" is never seen.
        Strings(match.Metadata["top_level_keys_preview"]).Should().Equal("title", "enabled", "servers", "nested");
    }

    [Fact]
    public void YamlKubernetesManifestDetection()
    {
        const string content = """

                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: app-config
                data:
                  key: value
                
            """;
        var match = Detect("manifest.yml", content);
        match.Should().NotBeNull();
        match!.Variant.Should().Be("kubernetes-manifest");
        match.Reasons.Should().Contain(reason => reason.Contains("apiVersion", StringComparison.Ordinal));
        match.Confidence.Should().BeApproximately(0.9, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .yml suggests YAML content",
            "Found key: value pairs indicative of YAML",
            "Found indented nested key: value blocks",
            "Detected apiVersion and kind keys typical of Kubernetes");
        Strings(match.Metadata!["top_level_keys_preview"]).Should().Equal("apiVersion", "kind", "metadata", "data");
    }

    [Fact]
    public void YamlByContentInConfFilename()
    {
        const string content = """

                storage:
                  dbPath: /var/lib/data
                net:
                  port: 27017
                
            """;
        var match = Detect("mongod.conf", content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("yaml");
        match.Variant.Should().BeOneOf("generic", "kubernetes-manifest");
        match.Confidence.Should().BeApproximately(0.7, 1e-9);
        match.Reasons.Should().Equal("Found key: value pairs indicative of YAML", "Found indented nested key: value blocks");
        Strings(match.Metadata!["top_level_keys_preview"]).Should().Equal("storage", "net");
    }

    [Fact]
    public void YamlHeavilyCommentedReferenceLikeMinion()
    {
        const string content = """

                # master: salt
                # user: root
                # cachedir: /var/cache/salt/minion
                # ipv6: false
                # minion_id_caching: true
                # append_domain: example.com
                # grains:
                #   roles: [webserver]
                #
                # log_level: info
                #
                # Later in the file, uncomment to set:
                # master: salt-master.internal
                
            """;
        var match = Detect("minion", content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("yaml");
        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.56, 1e-9);
        match.Reasons.Should().Equal("Found numerous commented YAML key: value examples");
        match.Metadata.Should().BeNull();
    }

    [Fact]
    public void YamlMultidocumentReportsMarkersAndIndentation()
    {
        const string content = """
            ---
            kind: ConfigMap
            metadata:
              name: sample
            ...
            ---
            apiVersion: v1
            kind: Pod
            spec:
                containers:
                  - name: app
                    image: demo:1

            """;
        var match = Detect("bundle.yaml", content);
        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("document start", StringComparison.OrdinalIgnoreCase));
        match.Reasons.Should().Contain(reason => reason.Contains("document end", StringComparison.OrdinalIgnoreCase));
        var indentation = Indentation(match);
        indentation["baseline"].Should().BeOneOf(2, 4);
        Ints(indentation["allowed_widths"]).Should().NotBeEmpty();

        match.Variant.Should().Be("kubernetes-manifest");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .yaml suggests YAML content",
            "Detected YAML document start marker '---'",
            "Detected YAML document end marker '...'",
            "Detected YAML list marker '- '",
            "Found key: value pairs indicative of YAML",
            "Found indented nested key: value blocks",
            "Detected apiVersion and kind keys typical of Kubernetes");
        indentation["style"].Should().Be("spaces");
        indentation["baseline"].Should().Be(2);
        Ints(indentation["allowed_widths"]).Should().Equal(2, 4, 6, 8);
        Strings(match.Metadata!["top_level_keys_preview"]).Should().Equal("kind", "metadata", "apiVersion", "spec", "image");
        match.Metadata.Should().NotContainKey("needs_review");
    }

    // Adversarial cases beyond the Python suite; every expected value was read from the Python plugin.
    [Fact]
    public void ValuelessKeySwallowsTheNextLine()
    {
        var match = Detect("a.yaml", "a:\nb: 1\n");
        match!.Confidence.Should().BeApproximately(0.75, 1e-9);
        match.Reasons.Should().Equal("File extension .yaml suggests YAML content", "Found key: value pairs indicative of YAML");
        match.Metadata!.Keys.Should().Equal("top_level_keys_preview");
        Strings(match.Metadata["top_level_keys_preview"]).Should().Equal("a");

        var nested = Detect("b.yaml", "a:\n  b: 1\n  c: 2\n");
        nested!.Confidence.Should().BeApproximately(0.85, 1e-9);
        Strings(nested.Metadata!["top_level_keys_preview"]).Should().Equal("a", "c");
        var indentation = Indentation(nested);
        indentation.Keys.Should().Equal("style", "baseline", "allowed_widths");
        Ints(indentation["allowed_widths"]).Should().Equal(2);
    }

    [Fact]
    public void ThreeNewlinesBeforeATopLevelKeyCountAsIndentedBlock()
    {
        var match = Detect("tripnl.yaml", "a: 1\n\n\nb: 2\n");
        match!.Confidence.Should().BeApproximately(0.85, 1e-9);
        match.Reasons.Should().Contain("Found indented nested key: value blocks");
        match.Metadata!.Keys.Should().Equal("top_level_keys_preview");
    }

    [Fact]
    public void ListMarkerMayCrossALineBreak()
    {
        var match = Detect("listnl.yaml", "-\nfoo\n");
        match!.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        match.Reasons.Should().Equal("File extension .yaml suggests YAML content", "Detected YAML list marker '- '");
        match.Metadata.Should().BeNull();
        Detect("ext.yml", "- x\n")!.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        Detect("ext2.yml", "just text\n").Should().BeNull();
        Detect("plain.txt", "a: 1\nb: 2\n").Should().BeNull();
    }

    // MULTILINE ^ anchors only after \n: with bare \r line endings only the first line can match, but once a leading
    // comment is skipped the scan text is re-joined with \n and every line anchors.
    [Fact]
    public void CarriageReturnLineEndingsAnchorOnlyAfterRejoin()
    {
        var crlf = Detect("crlf.yaml", "---\r\na: 1\r\nb: 2\r\n");
        crlf!.Confidence.Should().BeApproximately(0.8, 1e-9);
        Strings(crlf.Metadata!["top_level_keys_preview"]).Should().Equal("a", "b");
        Detect("cr.yaml", "---\ra: 1\rb: 2\r").Should().BeNull();
        var rejoined = Detect("cr2.yaml", "# c\r---\ra: 1\rb: 2\r");
        rejoined!.Confidence.Should().BeApproximately(0.8, 1e-9);
        rejoined.Reasons.Should().Contain("Detected YAML document start marker '---'");
        Strings(rejoined.Metadata!["top_level_keys_preview"]).Should().Equal("a", "b");
    }

    // Python [\w.-]: a superscript digit (No) and an astral letter continue the key, a combining mark (Mn) ends it.
    [Fact]
    public void KeyTokensUsePythonWordCharacters()
    {
        var match = Detect("u.yaml", "k\u00FC: 1\nk\u2082: 2\nk\u0303: 3\nk\U0001D41A: 4\n");
        match!.Confidence.Should().BeApproximately(0.75, 1e-9);
        Strings(match.Metadata!["top_level_keys_preview"]).Should().Equal("k\u00FC", "k\u2082", "k\U0001D41A");
    }

    [Fact]
    public void MinionFallbackNeedsSixCommentedKeys()
    {
        Detect("minion", "# a: 1\n# b: 2\n# c: 3\n# d: 4\n# e: 5\n").Should().BeNull();
        var match = Detect("minion", "# a: 1\n# b: 2\n# c: 3\n# d: 4\n# e: 5\n# f: 6\n");
        match!.Confidence.Should().BeApproximately(0.56, 1e-9);
        match.Metadata.Should().BeNull();
        // The trailing \s* of a commented key swallows the next line's indentation, so an indented follower is not counted.
        Detect("minion", "# a:\n  # b: 2\n# c: 3\n# d: 4\n# e: 5\n# f: 6\n").Should().BeNull();
        Detect("minion", "# a:\n# b: 2\n# c: 3\n# d: 4\n# e: 5\n# f: 6\n").Should().NotBeNull();
    }

    [Fact]
    public void AllowedWidthsIncludeMultiplesAndOffByTwo()
    {
        var match = Detect("nested.yaml", "# lead\n\na:\n  b:\n    c: 1\n  d:\n        e: 2\n   f: 3\n");
        match!.Confidence.Should().BeApproximately(0.85, 1e-9);
        var indentation = Indentation(match);
        indentation.Keys.Should().Equal("style", "baseline", "allowed_widths");
        indentation["baseline"].Should().Be(2);
        Ints(indentation["allowed_widths"]).Should().Equal(2, 3, 4, 8);
        Strings(match.Metadata!["top_level_keys_preview"]).Should().Equal("a", "c", "d", "f");
    }

    [Fact]
    public void TabAndMixedIndentationProfiles()
    {
        var tabs = Detect("tabsonly.yaml", "a:\n\tb: 1\n\tc: 2\n");
        tabs!.Confidence.Should().BeApproximately(0.75, 1e-9);
        var tabProfile = Indentation(tabs);
        tabProfile.Keys.Should().Equal("style", "tab_lines");
        tabProfile["style"].Should().Be("tabs");
        Ints(tabProfile["tab_lines"]).Should().Equal(2, 3);
        Strings(tabs.Metadata!["review_reasons"]).Should().Equal("Tab indentation present in YAML-like content");

        var mixed = Detect("mixed.yaml", "a:\n \tb: 1\n  c: 2\n");
        mixed!.Confidence.Should().BeApproximately(0.85, 1e-9);
        var mixedProfile = Indentation(mixed);
        mixedProfile.Keys.Should().Equal("style", "baseline", "allowed_widths", "mixed_indent_lines");
        mixedProfile["style"].Should().Be("mixed");
        Ints(mixedProfile["mixed_indent_lines"]).Should().Equal(2);
        mixed.Metadata!.Keys.Should().Equal("indentation", "top_level_keys_preview", "needs_review", "review_reasons");
    }

    [Fact]
    public void NullTextReturnsNull()
    {
        new YamlPlugin().Detect("x.yaml", [], null).Should().BeNull();
    }

    // Python re-scans the run from every line start (quadratic); the port skips each whitespace run once.
    [Theory]
    [InlineData(50000, "\n")]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "a: 1\nb: 2\nc: 3\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.yml", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.75, 1e-9);
        match.Reasons.Should().Equal("File extension .yml suggests YAML content", "Found key: value pairs indicative of YAML");
        match.Metadata!["top_level_keys_preview"].Should().BeAssignableTo<IEnumerable<string>>().Subject.Should().Equal("a", "b", "c");
    }
}
