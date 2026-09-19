using System.Text;

using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>Hunt rules over dynamic tokens.</summary>
[Collection(HuntSeamCollection.Name)]
public sealed class DynamicTokensTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-tokens-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void PlanTransformsCaptureRegexGroups()
    {
        var target = Write("settings.config", "connectionString=Server=db.internal.local;Database=Main;");
        var rule = new HuntRule("connection-string", "Capture database host", "database_server", patterns: ["Server=([^;]+)"]);

        var hits = HuntEngine.ExtractHits(File.ReadAllText(target), rule, target, TestContext.Current.CancellationToken);
        var transforms = HuntEngine.BuildPlanTransforms(hits);

        transforms.Should().ContainSingle();
        var transform = transforms[0];
        transform.TokenName.Should().Be("database_server");
        transform.Value.Should().Be("db.internal.local");
        transform.Placeholder.Should().Be("{{ database_server }}");
    }

    [Fact]
    public void PlanTransformTemplateOverride()
    {
        var target = Write("settings.config", "connectionString=Server=db.service;Database=Main;");
        var rule = new HuntRule("connection-string", "Capture database host", "database_server", patterns: ["Server=([^;]+)"]);

        var hits = HuntEngine.ExtractHits(File.ReadAllText(target), rule, target, TestContext.Current.CancellationToken);
        var transforms = HuntEngine.BuildPlanTransforms(hits, placeholderTemplate: "<<{token_name}>>");

        transforms.Should().ContainSingle();
        transforms[0].Placeholder.Should().Be("<<database_server>>");
    }

    [Fact]
    public void HuntJsonIncludesPlanTransformMetadata()
    {
        var target = Write("config.txt", "Server host: app.local");

        var hits = HuntEngine.ToHits(HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken));
        var transform = hits.First(hit => string.Equals(hit.Rule.Name, "server-name", StringComparison.Ordinal)).Metadata!.PlanTransform!;

        transform.TokenName.Should().Be("server_name");
        transform.Value.Should().Be("app.local");
        transform.Placeholder.Should().Be("{{ server_name }}");
    }
}
