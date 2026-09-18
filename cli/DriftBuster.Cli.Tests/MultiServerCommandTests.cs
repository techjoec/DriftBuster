using System.Text;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <c>driftbuster multi-server</c>: the request on stdin, progress and result lines on stdout
/// as <c>json.dumps(record, ensure_ascii=True)</c>, error lines with exit code 1.
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
              {"host_id": "a", "label": "Caf\u00e9 \\ A \ud800", "roots": [{{{Quote(hostA)}}}], "baseline": {"is_preferred": true}},
              {"host_id": "b", "label": "B", "roots": [{{{Quote(hostB)}}}]}]}
            """;

        var run = CliInvocation.InvokeWithInput(request, "multi-server");

        run.ExitCode.Should().Be(0, run.Out);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().OnlyContain(line => line.All(ch => ch < 0x7F));
        lines[..^1].Should().NotBeEmpty().And.OnlyContain(line => line.StartsWith("{\"type\": \"progress\", \"payload\": {\"host_id\": ", StringComparison.Ordinal));
        lines[^1].Should().StartWith("{\"type\": \"result\", \"payload\": {\"version\": \"multi-server.v2\", \"results\": [{\"host_id\": \"a\", \"label\": \"Caf\\u00e9 \\\\ A \\ud800\"");
        var result = JsonDocument.Parse(lines[^1]).RootElement.GetProperty("payload");
        result.GetProperty("summary").GetProperty("baseline_host_id").GetString().Should().Be("a");
        result.GetProperty("summary").GetProperty("generated_at").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{6})?\+00:00$");
        result.GetProperty("catalog").GetArrayLength().Should().Be(1);
        Directory.EnumerateFiles(cache).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("not json", "Invalid JSON payload: invalid JSON document")]
    [InlineData("[1]", "Unhandled error: expected a JSON object, not 'array'")]
    [InlineData("{\"schema_version\": 2}", "Unsupported schema version: 2")]
    [InlineData("{\"plans\": 5}", "'plans' must be an array")]
    [InlineData("{\"cache_dir\": 5}", "Unhandled error: cache_dir must be a path string, not 'integer'")]
    public void RefusedRequestsWriteOneErrorLine(string request, string message)
    {
        var run = CliInvocation.InvokeWithInput(request, "multi-server");

        run.ExitCode.Should().Be(1);
        run.Out.Should().Be($"{{\"type\": \"error\", \"message\": \"{message}\"}}{Environment.NewLine}");
    }
}
