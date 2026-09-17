using System.Text;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.Tests.Sql;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// The capture, SQL export and report operations of <see cref="DriftbusterBackend"/> the console tool and the PowerShell module call, over
/// files in a temporary directory.
/// </summary>
[Collection(CaptureSeamCollection.Name)]
public sealed class CaptureFacadeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-capture-facade-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public async Task FacadeRunsACaptureAndComparesItWithItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "tree")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), """{"Server": "db.corp.local", "Version": "1.2.3"}""", new UTF8Encoding(false), ct);
        IDriftbusterBackend backend = new DriftbusterBackend();
        var request = new CaptureRunRequest
        {
            Root = root,
            OutputDir = Path.Combine(_tmp.FullName, "out"),
            CaptureId = "cap",
            Operator = "tester",
            Environment = "lab",
            Reason = "facade",
            MaskTokens = ["corp"],
        };

        var run = await backend.RunCaptureAsync(request, ct);
        run.ExitCode.Should().Be(0);
        run.Output.Should().Contain("Snapshot written to").And.Contain("cap-manifest.json");
        run.Errors.Should().BeEmpty();
        File.Exists(run.SnapshotPath).Should().BeTrue();
        run.ManifestJson.Should().Contain("\"schema_version\": \"1.0\"");

        var refused = await backend.RunCaptureAsync(new CaptureRunRequest { Root = root, OutputDir = request.OutputDir }, ct);
        refused.ExitCode.Should().Be(1);
        refused.Errors.Should().Be("error: provide at least one --mask-token or explicitly opt-in with --allow-unmasked\n");
        refused.ManifestJson.Should().BeNull();

        var compared = await backend.CompareCapturesAsync(run.SnapshotPath!, run.SnapshotPath!, ct);
        compared.ExitCode.Should().Be(0);
        compared.Output.Should().StartWith("Snapshot comparison summary\n").And.Contain("Changed detections: 0\n");
        compared.ComparisonJson.Should().Contain("\"added_keys\": []");

        var first = await backend.CompareCapturesAsync(Path.Combine(_tmp.FullName, "none.json"), run.SnapshotPath!, ct);
        first.Output.Should().Be("No baseline snapshot found; record this run as the first capture.\n");
        first.ComparisonJson.Should().BeNull();
    }

    [Fact]
    public async Task FacadeExportsSqlSnapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = SqlTestDatabase.CreateSampleDatabase(Path.Combine(_tmp.FullName, "sample.sqlite"));
        IDriftbusterBackend backend = new DriftbusterBackend();

        var result = await backend.ExportSqlSnapshotAsync(
            new SqlExportRequest
            {
                Databases = [database, Path.Combine(_tmp.FullName, "missing.sqlite")],
                OutputDir = Path.Combine(_tmp.FullName, "exports"),
                MaskColumns = ["accounts.secret"],
                Limit = 1,
                Prefix = "demo",
            },
            ct);

        result.ExitCode.Should().Be(1);
        result.SnapshotPaths.Should().ContainSingle().Which.Should().EndWith("demo-sample-sql-snapshot.json");
        result.Output.Should().Be($"Exported SQL snapshot to {result.SnapshotPaths[0]}\n");
        result.Errors.Should().StartWith("error: database not found: ");
        result.ManifestPath.Should().EndWith("sql-manifest.json");
        result.ManifestJson.Should().Contain("\"limit\": 1").And.Contain("\"output\": \"demo-sample-sql-snapshot.json\"");
    }

    [Fact]
    public async Task FacadeBuildsHtmlAndJsonLinesReports()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "tree")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "app.ini"), "[server]\nhost = db01.secret.local\n", new UTF8Encoding(false), ct);
        IDriftbusterBackend backend = new DriftbusterBackend();
        var output = Path.Combine(_tmp.FullName, "report.html");

        var html = await backend.BuildReportAsync(new ReportRequest { Root = root, MaskTokens = ["secret"], OutputPath = output, Title = "Lab <run>" }, ct);
        html.Format.Should().Be("html");
        html.DetectionCount.Should().Be(1);
        html.HuntHitCount.Should().Be(1);
        html.Content.Should().Contain("Lab &lt;run&gt;").And.NotContain("db01.secret.local");
        (await File.ReadAllTextAsync(output, ct)).Should().Be(html.Content.Replace("\n", Environment.NewLine, StringComparison.Ordinal));

        var lines = await backend.BuildReportAsync(new ReportRequest { Format = "jsonl", Root = root, IncludeHunt = false }, ct);
        lines.OutputPath.Should().BeNull();
        lines.HuntHitCount.Should().Be(0);
        lines.Content.Should().EndWith("\n").And.Contain("\"type\": \"detection\"");

        var unsupported = () => backend.BuildReportAsync(new ReportRequest { Format = "pdf", Root = root }, ct);
        await unsupported.Should().ThrowAsync<ArgumentException>().WithMessage("Unsupported report format: pdf*");
    }

    [Fact]
    public async Task FacadeRefusesNullRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        IDriftbusterBackend backend = new DriftbusterBackend();
        await FluentActions.Awaiting(() => backend.ExportSqlSnapshotAsync(null!, ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => backend.RunCaptureAsync(null!, ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => backend.CompareCapturesAsync(null!, "b", ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => backend.SearchRegistryAsync(null!, ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => backend.BuildReportAsync(null!, ct)).Should().ThrowAsync<ArgumentNullException>();
    }
}
