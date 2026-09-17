using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The yaml plugin.</summary>
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
        var indentation = Indentation(match);
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

    [Fact]
    public void NullTextReturnsNull()
    {
        new YamlPlugin().Detect("x.yaml", [], null).Should().BeNull();
    }

    [Theory]
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
