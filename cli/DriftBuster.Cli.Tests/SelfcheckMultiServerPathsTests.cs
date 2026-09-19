using System.Text.Json;

using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <see cref="SelfcheckMultiServerPaths"/> (<c>driftbuster maint selfcheck-multi-server-paths</c>). The scenarios run in process, so the
/// portable root supplies only samples, preferred over the repository fixtures by <see cref="SelfcheckMultiServerPaths.ResolveSamples"/>.
/// </summary>
public sealed class SelfcheckMultiServerPathsTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-selfcheck-test-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void ResolveSamplesPrefersPortableSamples()
    {
        var portableRoot = Path.Combine(_tmp.FullName, "portable");
        Directory.CreateDirectory(Path.Combine(portableRoot, "Samples", "MultiServer"));
        var repo = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, "fixtures", "multi-server"));

        var resolved = SelfcheckMultiServerPaths.ResolveSamples(portableRoot, repo);

        resolved.Should().Be(Path.Combine(portableRoot, "Samples", "MultiServer"));
    }

    [Fact]
    public void MainWritesSuccessReport()
    {
        var output = Path.Combine(_tmp.FullName, "report.json");
        var portableRoot = Path.Combine(_tmp.FullName, "portable");
        Directory.CreateDirectory(Path.Combine(portableRoot, "Samples", "MultiServer"));
        using var stdout = new StringWriter();

        var code = SelfcheckMultiServerPaths.Run(
            portableRoot,
            output,
            _tmp.FullName,
            stdout,
            (_, _) =>
            [
                new ScenarioResult("a", true, "x=1"),
                new ScenarioResult("b", true, "x=2"),
            ]);

        code.Should().Be(0);
        using var payload = JsonDocument.Parse(File.ReadAllText(output));
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("passed").GetInt32().Should().Be(2);
        payload.RootElement.GetProperty("total").GetInt32().Should().Be(2);
    }
}
