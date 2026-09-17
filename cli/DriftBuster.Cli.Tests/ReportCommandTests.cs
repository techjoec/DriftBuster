using System.Text;

namespace DriftBuster.Cli.Tests;

/// <summary><c>driftbuster report</c>: the facade's HTML or JSON lines report, on stdout or in <c>--output</c>.</summary>
public sealed class ReportCommandTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-report-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Tree()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "tree")).FullName;
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{\"Server\": \"db.corp.local\", \"Host\": \"h\"}\n", Utf8);
        return root;
    }

    [Fact]
    public void JsonLinesGoToStdoutWithDetectionsAndHuntHits()
    {
        var run = CliInvocation.Invoke("report", Tree(), "--format", "jsonl", "--mask-token", "db.corp.local", "--placeholder", "[MASK]");

        run.ExitCode.Should().Be(0, run.Err);
        var records = run.JsonLines();
        records.Select(record => record.GetProperty("type").GetString()).Should().Equal("detection", "hunt_hit");
        run.Out.Should().Contain("[MASK]").And.NotContain("db.corp.local");
    }

    [Fact]
    public void HtmlIsWrittenToTheOutputFile()
    {
        var output = Path.Combine(_tmp.FullName, "report.html");

        var run = CliInvocation.Invoke("report", Tree(), "--output", output, "--title", "Nightly", "--skip-hunt");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Be($"Report written to {output}{Environment.NewLine}");
        File.ReadAllText(output, Utf8).Should().StartWith("<!doctype html>").And.Contain("Nightly");
    }

    [Fact]
    public void AnUnknownFormatIsAParseError()
    {
        var run = CliInvocation.Invoke("report", Tree(), "--format", "pdf");

        run.ExitCode.Should().Be(2);
        run.Err.Should().Contain("pdf");
    }
}
