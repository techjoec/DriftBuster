using System.Globalization;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend;

/// <summary>The capture, SQL export, registry and report surfaces the console tool and the PowerShell module call.</summary>
public sealed partial class DriftbusterBackend
{
    private static readonly UTF8Encoding ReportEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public Task<SqlExportResult> ExportSqlSnapshotAsync(SqlExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(
            () =>
            {
                var (stdout, stderr) = Writers();
                var runner = new CaptureRunner();
                var exitCode = runner.ExportSql(options, stdout, stderr);
                return new SqlExportResult(exitCode, stdout.ToString(), stderr.ToString(), runner.LastExport?.ManifestPath, runner.LastExport?.Manifest, runner.LastExport?.SnapshotPaths ?? []);
            },
            cancellationToken);
    }

    public Task<CaptureRunResult> RunCaptureAsync(CaptureRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(
            () =>
            {
                var (stdout, stderr) = Writers();
                var runner = new CaptureRunner();
                var exitCode = runner.Run(options, stdout, stderr);
                return new CaptureRunResult(exitCode, stdout.ToString(), stderr.ToString(), runner.LastRun?.SnapshotPath, runner.LastRun?.ManifestPath, runner.LastRun?.Manifest);
            },
            cancellationToken);
    }

    public Task<CaptureCompareResult> CompareCapturesAsync(string baselinePath, string currentPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselinePath);
        ArgumentNullException.ThrowIfNull(currentPath);
        return Task.Run(
            () =>
            {
                var (stdout, stderr) = Writers();
                var runner = new CaptureRunner();
                var exitCode = runner.Compare(baselinePath, currentPath, stdout, stderr);
                return new CaptureCompareResult(exitCode, stdout.ToString(), stderr.ToString(), runner.LastComparison);
            },
            cancellationToken);
    }

    private static (StringWriter Stdout, StringWriter Stderr) Writers()
        => (new StringWriter(CultureInfo.InvariantCulture), new StringWriter(CultureInfo.InvariantCulture));

    /// <remarks>Off Windows this fails with the default registry backend's <c>Windows Registry scanning requires Windows platform</c>.</remarks>
    public Task<RegistryAppListResult> ListRegistryAppsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => new RegistryAppListResult { Apps = RegistryOperations.EnumerateInstalledApps().Select(ToRegistryApplication).ToArray() },
            cancellationToken);
    }

    /// <remarks>
    /// Order: the installed applications are enumerated first (off Windows this fails with the default registry backend's
    /// <c>Windows Registry scanning requires Windows platform</c>), then the explicit roots are parsed or the token's roots suggested, the
    /// patterns compiled, and the roots searched.
    /// </remarks>
    public Task<RegistrySearchResult> SearchRegistryAsync(RegistrySearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () =>
            {
                var apps = RegistryOperations.EnumerateInstalledApps();
                var explicitRoots = request.Roots.Select(RegistryRoot.Parse).ToList();
                var roots = explicitRoots.Count > 0 ? explicitRoots : RegistryOperations.FindAppRegistryRoots(request.Token, apps);
                var spec = new SearchSpec
                {
                    Keywords = request.Keywords.ToList(),
                    Patterns = request.Patterns.Select(RegistryText.Compile).ToList(),
                    MaxDepth = request.MaxDepth,
                    MaxHits = request.MaxHits,
                    TimeBudgetS = request.TimeBudgetSeconds,
                };
                var hits = RegistryOperations.SearchRegistry(roots, spec);
                return new RegistrySearchResult
                {
                    Roots = roots.Select(root => new RegistryRootEntry { Hive = root.Hive, Path = root.Path, View = root.View }).ToArray(),
                    Hits = hits.Select(hit => new RegistrySearchHit
                    {
                        Hive = hit.Hive,
                        Path = hit.Path,
                        ValueName = hit.ValueName,
                        DataPreview = hit.DataPreview,
                        Reason = hit.Reason,
                    }).ToArray(),
                };
            },
            cancellationToken);
    }

    public Task<ReportResult> BuildReportAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => BuildReport(request, cancellationToken), cancellationToken);
    }

    // The tree's detections (and default-rule hunt hits) rendered once as the HTML page or JSON lines, each record followed by LF; the file
    // receives that text with the platform's line breaks.
    private static ReportResult BuildReport(ReportRequest request, CancellationToken cancellationToken)
    {
        var format = request.Format switch
        {
            "html" or "jsonl" => request.Format,
            _ => throw new ArgumentException($"Unsupported report format: {request.Format} (expected html or jsonl).", nameof(request)),
        };
        var detections = new Detector().ScanPath(request.Root, request.Glob)
            .Where(result => result.Match is not null)
            .Select(result => result.Match!.ToPayload(result.Path))
            .ToList();
        var root = Directory.Exists(request.Root) ? request.Root : null;
        List<HuntHitResult> huntHits = request.IncludeHunt
            ? [.. HuntEngine.HuntPath(request.Root, HuntRules.Default, request.Glob, cancellationToken: cancellationToken).Hits.Select(hit => HuntHitResult.From(hit, root))]
            : [];
        var redactor = RedactionFilter.Resolve(maskTokens: request.MaskTokens, placeholder: request.Placeholder);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        if (string.Equals(format, "html", StringComparison.Ordinal))
        {
            writer.Write(HtmlReport.Render(request.Title, detections, huntHits, redactor, TimeProvider.System.GetUtcNow()));
        }
        else
        {
            JsonLinesReport.Write(writer, detections, huntHits, redactor);
        }

        var content = writer.ToString();
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            EngineTextFile.WriteBytes(request.OutputPath, ReportEncoding.GetBytes(content.ReplaceLineEndings()));
        }

        return new ReportResult
        {
            Format = format,
            Content = content,
            OutputPath = string.IsNullOrWhiteSpace(request.OutputPath) ? null : request.OutputPath,
            DetectionCount = detections.Count,
            HuntHitCount = huntHits.Count,
        };
    }

    private static RegistryApplication ToRegistryApplication(RegistryApp app) => new()
    {
        DisplayName = app.DisplayName,
        KeyPath = app.KeyPath,
        Hive = app.Hive,
        Publisher = app.Publisher,
        Version = app.Version,
        UninstallString = app.UninstallString,
        InstallLocation = app.InstallLocation,
        View = app.View,
    };
}
