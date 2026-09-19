using System.Text.Json;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>Captures: refusals, redacted snapshot and manifest, comparison, and the backend facade.</summary>
public sealed class CaptureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 3, 1, 8, 30, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-capture-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Root => Path.Join(_tmp.FullName, "site-db01");

    private string Output => Path.Join(_tmp.FullName, "captures");

    private CaptureRunOptions Options(string id) => new()
    {
        Root = Root,
        OutputDir = Output,
        CaptureId = id,
        Operator = "ops",
        Environment = "lab",
        Reason = "audit",
        MaskTokens = ["site-db01"],
    };

    private void Write(string relative, string text)
    {
        var path = Path.Join(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private (int ExitCode, string Out, string Err, CaptureRunner Runner) Run(CaptureRunOptions options)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var runner = new CaptureRunner(new FixedTimeProvider(Now), () => "host-1");
        var exitCode = runner.Run(options, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString(), runner);
    }

    [Theory]
    [InlineData("missing-root", "error: capture root does not exist: *")]
    [InlineData("no-mask", "error: provide at least one --mask-token or explicitly opt-in with --allow-unmasked\n")]
    [InlineData("no-environment", "error: --environment is required for capture manifests\n")]
    [InlineData("no-reason", "error: --reason is required for capture manifests\n")]
    public void Runs_without_the_required_context_are_refused(string refusal, string message)
    {
        Directory.CreateDirectory(Root);
        var options = refusal switch
        {
            "missing-root" => Options("x") with { Root = Path.Join(_tmp.FullName, "nowhere") },
            "no-mask" => Options("x") with { MaskTokens = [] },
            "no-environment" => Options("x") with { Environment = " " },
            _ => Options("x") with { Reason = null },
        };

        var run = Run(options);

        run.ExitCode.Should().Be(1);
        run.Err.Should().Match(message);
        Directory.Exists(Output).Should().BeFalse();
    }

    [Fact]
    public void A_capture_writes_a_redacted_snapshot_and_its_manifest()
    {
        Write("app/appsettings.json", """{"ConnectionStrings": {"Main": "Server=db01.internal;Database=app"}}""");
        Write("web.config", """<configuration><appSettings><add key="Api" value="https://db01.internal/api" /></appSettings></configuration>""");

        var run = Run(Options("first"));

        run.ExitCode.Should().Be(0, run.Err);
        var (snapshotPath, manifestPath, manifest) = run.Runner.LastRun!.Value;
        run.Out.Should().Be($"Snapshot written to {snapshotPath}\nManifest written to {manifestPath}\n");
        File.ReadAllText(snapshotPath).Should().NotContain("site-db01");
        var snapshot = CaptureRunner.ReadSnapshot(snapshotPath);
        snapshot.Capture.Should().Be(new CaptureInfo("first", Root.Replace("site-db01", "[REDACTED]", StringComparison.Ordinal), Now, "ops", "lab", "audit", "host-1", "[REDACTED]", 1));
        snapshot.Detections.Select(detection => detection.RelativePath).Should().BeEquivalentTo(["app/appsettings.json", "web.config"]);
        manifest.Counts.Detections.Should().Be(2);
        manifest.Redaction.TotalRedactions.Should().BePositive();
        manifest.Capture.SnapshotFile.Should().Be("first-snapshot.json");
        JsonSerializer.Deserialize(File.ReadAllText(manifestPath), ModelJson.TypeInfo<CaptureManifest>()).Should().BeEquivalentTo(manifest);
    }

    [Fact]
    public void Comparing_captures_reports_added_removed_and_changed_detections()
    {
        Write("a.json", """{"k": 1}""");
        Write("b.ini", "[s]\nk=v\n");
        var first = Run(Options("first")).Runner.LastRun!.Value.SnapshotPath;
        File.Delete(Path.Join(Root, "b.ini"));
        Write("a.json", """{"k": 1, "z": [1, 2, 3], "y": {"deep": true}}""");
        Write("c.yaml", "key: value\nother: 2\n");
        var second = Run(Options("second")).Runner.LastRun!.Value.SnapshotPath;
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var runner = new CaptureRunner();

        runner.Compare(first, second, stdout, stderr).Should().Be(0, stderr.ToString());

        var comparison = runner.LastComparison!;
        comparison.AddedKeys.Select(key => key.Location).Should().Equal("c.yaml");
        comparison.RemovedKeys.Select(key => key.Location).Should().Equal("b.ini");
        comparison.ChangedKeys.Select(key => key.Location).Should().Equal("a.json");
        stdout.ToString().Should().StartWith("Snapshot comparison summary\n").And.Contain("Changed detections: 1\n").And.Contain("Profile summary diff unavailable");

        runner.Compare(Path.Join(_tmp.FullName, "none.json"), second, stdout, stderr).Should().Be(0);
        runner.Compare(first, Path.Join(_tmp.FullName, "none.json"), stdout, stderr).Should().Be(1);
    }

    [Fact]
    public async Task The_backend_facade_runs_captures()
    {
        Write("a.json", """{"k": 1}""");
        IDriftbusterBackend backend = new DriftbusterBackend();

        var result = await backend.RunCaptureAsync(Options("facade"), TestContext.Current.CancellationToken);

        result.ExitCode.Should().Be(0, result.Errors);
        result.Manifest!.Counts.Detections.Should().Be(1);
        var compared = await backend.CompareCapturesAsync(result.SnapshotPath!, result.SnapshotPath!, TestContext.Current.CancellationToken);
        compared.Comparison!.ChangedKeys.Should().BeEmpty();
    }
}
