using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// <see cref="HuntEngine"/>: hits, exclusions, the JSON payload, XML attribute tokens and binary files.
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-");

    public void Dispose()
    {
        HuntEngine.RelativeTo = LexicalPath.RelativeTo;
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

        var hits = HuntEngine.ToHits(HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken));

        hits.Should().NotBeEmpty();
        hits[0].Rule.Name.Should().Be("server-name");
        hits[0].RelativePath.Should().EndWith("config.txt");
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
    public void ExtractHitsWithoutPatterns()
    {
        var rule = new HuntRule("basic", "match anything");
        var target = TmpPath("sample.txt");
        WriteText(target, "alpha\nbeta");

        var hits = HuntEngine.ExtractHits(File.ReadAllText(target), rule, target, TestContext.Current.CancellationToken);

        hits.Should().HaveCount(2);
        hits.Select(hit => hit.Excerpt).Should().BeEquivalentTo(["alpha", "beta"]);
    }
}
