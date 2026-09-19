using System.Text;

namespace DriftBuster.Cli.Tests;

/// <summary><c>driftbuster hunt</c>: hits as one JSON array, readable by <c>detection-profile hunt-bridge</c>.</summary>
public sealed class HuntCommandTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void HitsArePrintedAsOneJsonArray()
    {
        var app = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "appsettings.json"), "{\"Server\": \"db.corp.local\", \"Host\": \"café\"}\n", Utf8);
        File.WriteAllText(Path.Combine(app, "skip.txt"), "server host: other.corp.local\n", Utf8);

        var run = CliInvocation.Invoke("hunt", _tmp.FullName, "--exclude", "*.txt");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().StartWith("[{\"rule\":{").And.EndWith("]" + Environment.NewLine).And.Contain("café", "non-ASCII is written as is");
        run.Out.TrimEnd().Should().NotContain("\n", "the hits are one line");
        var hits = run.Json();
        hits.GetArrayLength().Should().Be(1);
        hits[0].GetProperty("relative_path").GetString().Should().Be("app/appsettings.json");
        hits[0].GetProperty("rule").GetProperty("name").GetString().Should().Be("server-name");
        hits[0].GetProperty("metadata").GetProperty("plan_transform").GetProperty("placeholder").GetString().Should().Be("{{ server_name }}");

        var store = Path.Combine(_tmp.FullName, "store.json");
        var huntFile = Path.Combine(_tmp.FullName, "hunt.json");
        File.WriteAllText(store, """{"profiles": [{"name": "prod", "configs": [{"id": "cfg", "path": "app/appsettings.json"}]}]}""", Utf8);
        File.WriteAllText(huntFile, run.Out, Utf8);
        var bridge = CliInvocation.Invoke("detection-profile", "hunt-bridge", store, huntFile);
        bridge.ExitCode.Should().Be(0, bridge.Err);
        bridge.Json().GetProperty("items")[0].GetProperty("profiles")[0].GetProperty("config").GetString().Should().Be("cfg");
    }

    [Fact]
    public void AMissingRootHasNoHits()
    {
        var run = CliInvocation.Invoke("hunt", Path.Combine(_tmp.FullName, "missing"), "--placeholder-template", "<{token_name}>");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Be("[]" + Environment.NewLine);
    }
}
