using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Mirror of tests/hunt/test_hunt.py. The two monkeypatch tests swap <see cref="HuntEngine.RelativeTo"/>, the seam over
/// <c>Path.relative_to</c>: Python replaces <c>Path.relative_to</c> (and, in the JSON test, <c>Path.glob</c> so the walk
/// yields a wrapper whose <c>relative_to</c> raises); both reduce to "relative_to raises for the sample file".
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-");

    public void Dispose()
    {
        HuntEngine.RelativeTo = PythonPurePath.RelativeTo;
        _tmp.Delete(recursive: true);
    }

    private string TmpPath(params string[] segments) => Path.Combine([_tmp.FullName, .. segments]);

    private static void WriteText(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(false));

    [Fact]
    public void HuntPathReturnsHits()
    {
        var target = TmpPath("config.txt");
        WriteText(target, "Server host: api.corp.local\nThumbprint: 0123456789abcdef0123456789abcdef01234567");

        var results = HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken).Hits;

        results.Should().NotBeEmpty();
        results.Select(hit => hit.Rule.Name).Should().Contain("server-name");
    }

    [Fact]
    public void HuntPathRespectsExclusions()
    {
        var directory = TmpPath("configs");
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, "info.txt"), "Server host: db.prod.internal");

        var results = HuntEngine.HuntPath(directory, HuntRules.Default, excludePatterns: ["*.txt"], cancellationToken: TestContext.Current.CancellationToken).Hits;

        results.Should().BeEmpty();
    }

    [Fact]
    public void HuntPathJsonFormat()
    {
        var target = TmpPath("config.txt");
        WriteText(target, "Server host: infra.corp.net");

        var payload = HuntEngine.ToJson(HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken));

        payload.Should().NotBeEmpty();
        var entry = payload[0];
        ((OrderedDictionary<string, object?>)entry["rule"]!)["name"].Should().Be("server-name");
        ((string)entry["relative_path"]!).Should().EndWith("config.txt");
    }

    [Fact]
    public void HuntRulesCaptureXmlAttributeTokens()
    {
        var target = TmpPath("settings.config");
        WriteText(
            target,
            "\n" +
            "        <configuration>\n" +
            "          <connectionStrings>\n" +
            "            <add name=\"Primary\" connectionString=\"Server=sql.example.local;Database=App;\" />\n" +
            "          </connectionStrings>\n" +
            "          <appSettings>\n" +
            "            <add key=\"ServiceEndpoint\" value=\"https://api.example.com/v1/\" />\n" +
            "            <add key=\"FeatureFlag:NewDashboard\" value=\"true\" />\n" +
            "          </appSettings>\n" +
            "          <system.serviceModel>\n" +
            "            <client>\n" +
            "              <endpoint address=\"net.tcp://svc.example.local:9000/Feed\" />\n" +
            "            </client>\n" +
            "          </system.serviceModel>\n" +
            "        </configuration>\n" +
            "        ");

        var results = HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken).Hits;

        results.Should().NotBeEmpty();
        results.Select(hit => hit.Rule.Name).Should().Contain(["connection-string", "service-endpoint", "feature-flag"]);
    }

    [Fact]
    public void HuntPathSkipsBinaryFiles()
    {
        var target = TmpPath("binary.dat");
        File.WriteAllBytes(target, [0x00, 0xFF, 0x00, 0xFF]);

        HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken).Hits.Should().BeEmpty();
    }

    [Fact]
    public void KeywordAndExclusionHelpers()
    {
        HuntEngine.MatchesKeywords("Server host", ["server"]).Should().BeTrue();
        HuntEngine.MatchesKeywords("host", ["server"]).Should().BeFalse();

        var candidate = TmpPath("dir", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        WriteText(candidate, "data");
        var relative = PythonPurePath.RelativeTo(candidate, TmpPath("dir"));
        HuntEngine.ShouldExclude(candidate, relative, ["*.txt"]).Should().BeTrue();
    }

    [Fact]
    public void ExtractHitsWithoutPatterns()
    {
        var rule = new HuntRule("basic", "match anything");
        var target = TmpPath("sample.txt");
        WriteText(target, "alpha\nbeta");

        var hits = HuntEngine.ExtractHits(File.ReadAllText(target), rule, target, TestContext.Current.CancellationToken);

        hits.Should().HaveCount(2);
        hits.Select(hit => hit.Excerpt).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void ShouldExcludeRelativeOnly()
    {
        // The stub candidate never matches: the relative-only branch must decide on its own.
        HuntEngine.ShouldExclude(_ => false, "notes/entry.log", ["notes/entry.log"]).Should().BeTrue();
    }

    [Fact]
    public void HuntPathHandlesRelativeToErrors()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);
        var sample = Path.Combine(root, "config.txt");
        WriteText(sample, "server: host");

        var original = HuntEngine.RelativeTo;
        HuntEngine.RelativeTo = (path, other) => string.Equals(path, sample, StringComparison.Ordinal) ? null : original(path, other);

        var hits = HuntEngine.HuntPath(root, HuntRules.Default, excludePatterns: ["*.tmp"], cancellationToken: TestContext.Current.CancellationToken).Hits;
        hits.Should().BeAssignableTo<IReadOnlyList<HuntFinding>>();
    }

    [Fact]
    public void HuntJsonRelativeFallback()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);
        var sample = Path.Combine(root, "config.txt");
        WriteText(sample, "value");

        var original = HuntEngine.RelativeTo;
        HuntEngine.RelativeTo = (path, other) => string.Equals(path, sample, StringComparison.Ordinal) ? null : original(path, other);

        var rule = new HuntRule("any", "custom rule");
        var payload = HuntEngine.ToJson(HuntEngine.HuntPath(root, [rule], cancellationToken: TestContext.Current.CancellationToken));

        payload.Should().NotBeEmpty();
        payload[0]["relative_path"].Should().Be(Path.GetFileName(sample));
    }
}
