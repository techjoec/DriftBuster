using System.Text;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary><c>driftbuster capture run|compare|export-sql</c>: the files, text and exit codes of each subcommand.</summary>
public sealed class CaptureCommandTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-capture-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Tree(string server)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "trees", "app")).Parent!.FullName;
        File.WriteAllText(Path.Combine(root, "app", "appsettings.json"), $"{{\"Server\": \"{server}\", \"Version\": \"1.2.3\"}}\n", Utf8);
        return root;
    }

    private CliInvocation Capture(string root, string captureId, params string[] extra)
        => CliInvocation.Invoke(
        [
            "capture", "run", root, "--output-dir", Path.Combine(_tmp.FullName, "captures"), "--capture-id", captureId, "--operator", "tester",
            "--environment", "test", "--reason", "cli test", .. extra,
        ]);

    [Fact]
    public void RunWritesTheSnapshotAndManifestAndReportsBoth()
    {
        var run = Capture(Tree("db.corp.local"), "first", "--mask-token", "db.corp.local");

        run.ExitCode.Should().Be(0, run.Err);
        var snapshot = Path.Combine(_tmp.FullName, "captures", "first-snapshot.json");
        var manifest = Path.Combine(_tmp.FullName, "captures", "first-manifest.json");
        run.Out.Should().Be($"Snapshot written to {snapshot}{Environment.NewLine}Manifest written to {manifest}{Environment.NewLine}");
        run.Err.Should().BeEmpty();
        var snapshotText = File.ReadAllText(snapshot, Utf8);
        snapshotText.Should().NotContain("db.corp.local");
        JsonDocument.Parse(snapshotText).RootElement.GetProperty("detections").GetArrayLength().Should().Be(1);
        JsonDocument.Parse(File.ReadAllText(manifest, Utf8)).RootElement.GetProperty("redaction").GetProperty("total_redactions").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void RunRefusesAnUnmaskedCaptureWithExitOne()
    {
        var run = Capture(Tree("db.corp.local"), "refused");

        run.ExitCode.Should().Be(1);
        run.Out.Should().BeEmpty();
        run.Err.Should().Be("error: provide at least one --mask-token or explicitly opt-in with --allow-unmasked" + Environment.NewLine);
        Directory.Exists(Path.Combine(_tmp.FullName, "captures")).Should().BeFalse();
    }

    [Fact]
    public void CompareSummarisesTwoCaptures()
    {
        var root = Tree("db.corp.local");
        Capture(root, "before", "--allow-unmasked").ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(root, "app", "extra.json"), "{\"Version\": \"2.0.0\"}\n", Utf8);
        Capture(root, "after", "--allow-unmasked").ExitCode.Should().Be(0);
        var captures = Path.Combine(_tmp.FullName, "captures");

        var run = CliInvocation.Invoke("capture", "compare", Path.Combine(captures, "before-snapshot.json"), Path.Combine(captures, "after-snapshot.json"));

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().StartWith($"Snapshot comparison summary{Environment.NewLine}==========================={Environment.NewLine}Added detections: 1{Environment.NewLine}");
        run.Out.Should().Contain("Added detection keys:");

        var missing = CliInvocation.Invoke("capture", "compare", Path.Combine(captures, "none.json"), Path.Combine(captures, "after-snapshot.json"));
        missing.ExitCode.Should().Be(0);
        missing.Out.Should().Be("No baseline snapshot found; record this run as the first capture." + Environment.NewLine);

        var absent = CliInvocation.Invoke("capture", "compare", Path.Combine(captures, "before-snapshot.json"), Path.Combine(captures, "none.json"));
        absent.ExitCode.Should().Be(1);
        absent.Err.Should().Be($"error: current snapshot not found: {Path.Combine(captures, "none.json")}{Environment.NewLine}");
    }

    /// <summary><c>capture export-sql</c> writes <c>sql-manifest.json</c> without reporting it, and leaves a non-positive limit to the exporter.</summary>
    [Fact]
    public void ExportSqlRefusesANonPositiveLimitBeforeWriting()
    {
        var database = CliTests.CreateSqliteDb(Path.Combine(_tmp.FullName, "demo.sqlite"));
        var output = Path.Combine(_tmp.FullName, "exports");

        var run = CliInvocation.Invoke("capture", "export-sql", database, "--output-dir", output, "--limit", "0");

        run.ExitCode.Should().Be(1);
        run.Out.Should().BeEmpty();
        run.Err.Should().Be("error: --limit must be positive when provided" + Environment.NewLine);
        Directory.Exists(output).Should().BeFalse();
    }
}
