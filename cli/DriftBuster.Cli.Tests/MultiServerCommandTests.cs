using System.Text;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <c>driftbuster multi-server</c>: the request on stdin, progress and result lines on stdout, an error line with exit code 1.
/// </summary>
public sealed class MultiServerCommandTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Host(string name, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_tmp.FullName, name)).FullName;
        File.WriteAllText(Path.Combine(directory, "app.json"), content, Utf8);
        return directory;
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    [Fact]
    public void RequestOnStdinWritesProgressThenTheResult()
    {
        var hostA = Host("a", "{\"Mode\": \"on\"}\n");
        var hostB = Host("b", "{\"Mode\": \"off\"}\n");
        var cache = Path.Combine(_tmp.FullName, "cache");
        var request = $$$"""
            {"schema_version": "multi-server.v2", "cache_dir": {{{Quote(cache)}}}, "plans": [
              {"host_id": "a", "label": "Caf\u00e9 A", "roots": [{{{Quote(hostA)}}}], "baseline": {"is_preferred": true}},
              {"host_id": "b", "label": "B", "roots": [{{{Quote(hostB)}}}]}]}
            """;

        var run = CliInvocation.InvokeWithInput(request, "multi-server");

        run.ExitCode.Should().Be(0, run.Out);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines[..^1].Should().NotBeEmpty().And.OnlyContain(line => line.StartsWith("{\"type\":\"progress\",\"progress\":{\"host_id\":", StringComparison.Ordinal));
        var result = JsonDocument.Parse(lines[^1]).RootElement;
        result.GetProperty("type").GetString().Should().Be("result");
        var payload = result.GetProperty("result");
        payload.GetProperty("results")[0].GetProperty("label").GetString().Should().Be("Café A");
        payload.GetProperty("summary").GetProperty("baseline_host_id").GetString().Should().Be("a");
        payload.GetProperty("catalog").GetArrayLength().Should().Be(1);
        Directory.EnumerateFiles(cache).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("not json", "Invalid request: $: *")]
    [InlineData("[1]", "Invalid request: $: *")]
    [InlineData("{\"schema_version\": \"2\"}", "Unsupported schema version: 2")]
    [InlineData("{\"plans\": 5}", "Invalid request: $.plans: *")]
    [InlineData("{\"colour\": 5}", "Invalid request: $.colour: *")]
    public void RefusedRequestsWriteOneErrorLine(string request, string message)
    {
        var run = CliInvocation.InvokeWithInput(request, "multi-server");

        run.ExitCode.Should().Be(1);
        var line = JsonDocument.Parse(run.Out).RootElement;
        line.GetProperty("type").GetString().Should().Be("error");
        line.GetProperty("message").GetString().Should().Match(message);
    }
}
