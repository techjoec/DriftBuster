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

    public Task<SqlExportResult> ExportSqlSnapshotAsync(SqlExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () =>
            {
                var (stdout, stderr) = (new StringWriter(CultureInfo.InvariantCulture), new StringWriter(CultureInfo.InvariantCulture));
                var outcome = CaptureRunner.RunSqlExport(
                    new SqlExportOptions
                    {
                        Database = request.Databases,
                        OutputDir = request.OutputDir,
                        Table = request.Tables,
                        ExcludeTable = request.ExcludeTables,
                        MaskColumn = request.MaskColumns,
                        HashColumn = request.HashColumns,
                        Placeholder = request.Placeholder,
                        HashSalt = request.HashSalt,
                        Limit = request.Limit,
                        Prefix = request.Prefix,
                    },
                    stdout,
                    stderr);
                return new SqlExportResult
                {
                    ExitCode = outcome.ExitCode,
                    Output = stdout.ToString(),
                    Errors = stderr.ToString(),
                    ManifestPath = outcome.ManifestPath,
                    ManifestJson = Canonicaliser.DumpsSorted(outcome.Manifest, indent: true, ensureAscii: true),
                    SnapshotPaths = outcome.SnapshotPaths.ToArray(),
                };
            },
            cancellationToken);
    }

    public Task<CaptureRunResult> RunCaptureAsync(CaptureRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () =>
            {
                var (stdout, stderr) = (new StringWriter(CultureInfo.InvariantCulture), new StringWriter(CultureInfo.InvariantCulture));
                var outcome = CaptureRunner.RunCapture(ToCaptureOptions(request), stdout, stderr);
                return new CaptureRunResult
                {
                    ExitCode = outcome.ExitCode,
                    Output = stdout.ToString(),
                    Errors = stderr.ToString(),
                    SnapshotPath = outcome.SnapshotPath,
                    ManifestPath = outcome.ManifestPath,
                    ManifestJson = outcome.Manifest is null ? null : Canonicaliser.DumpsSorted(outcome.Manifest, indent: true, ensureAscii: true),
                };
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
                var (stdout, stderr) = (new StringWriter(CultureInfo.InvariantCulture), new StringWriter(CultureInfo.InvariantCulture));
                var comparison = CaptureRunner.CompareSnapshots(new CaptureCompareOptions(baselinePath, currentPath), stdout, stderr);
                return new CaptureCompareResult
                {
                    ExitCode = comparison.ExitCode,
                    Output = stdout.ToString(),
                    Errors = stderr.ToString(),
                    ComparisonJson = comparison.Payload is null ? null : Canonicaliser.Dumps(comparison.Payload, indent: true, ensureAscii: false, sortKeys: false),
                };
            },
            cancellationToken);
    }

    /// <remarks>Off Windows this fails with the default registry backend's <c>Windows Registry scanning requires Windows platform</c>.</remarks>
    public Task<RegistryAppListResult> ListRegistryAppsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => new RegistryAppListResult { Apps = RegistryOperations.EnumerateInstalledApps().Select(ToRegistryApplication).ToArray() },
            cancellationToken);
    }

    /// <remarks>
    /// The <c>registry search</c> order: the installed applications are enumerated first (off Windows this fails with the default registry
    /// backend's <c>Windows Registry scanning requires Windows platform</c>), then the explicit roots are parsed or the token's roots
    /// suggested, the patterns compiled as Python <c>re</c> patterns, and the roots searched.
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

    // The detections of the tree (and its default-rule hunt hits) rendered once: write_html_report's page, or write_json_lines' records each
    // followed by LF; the requested file receives that text in text mode.
    private static ReportResult BuildReport(ReportRequest request, CancellationToken cancellationToken)
    {
        var format = request.Format switch
        {
            "html" or "jsonl" => request.Format,
            _ => throw new ArgumentException($"Unsupported report format: {request.Format} (expected html or jsonl).", nameof(request)),
        };
        var matches = new Detector().ScanPath(request.Root, request.Glob)
            .Where(result => result.Match is not null)
            .Select(result => result.Match!)
            .ToList();
        var huntHits = request.IncludeHunt
            ? HuntEngine.HuntPath(request.Root, HuntRules.Default, request.Glob, cancellationToken: cancellationToken).Hits.Cast<object>().ToList()
            : [];
        var maskTokens = request.MaskTokens.ToList();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        if (string.Equals(format, "html", StringComparison.Ordinal))
        {
            HtmlReport.Write(matches, writer, request.Title, huntHits: huntHits, maskTokens: maskTokens, placeholder: request.Placeholder);
        }
        else
        {
            JsonLinesReport.WriteJsonLines(matches, writer, huntHits: huntHits, maskTokens: maskTokens, placeholder: request.Placeholder);
        }

        var content = writer.ToString();
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            EngineTextFile.WriteBytes(request.OutputPath, ReportEncoding.GetBytes(ReportValues.TextModeNewLines(content)));
        }

        return new ReportResult
        {
            Format = format,
            Content = content,
            OutputPath = string.IsNullOrWhiteSpace(request.OutputPath) ? null : request.OutputPath,
            DetectionCount = matches.Count,
            HuntHitCount = huntHits.Count,
        };
    }

    private static CaptureRunOptions ToCaptureOptions(CaptureRunRequest request) => new()
    {
        Root = request.Root,
        Profiles = request.ProfilesPath,
        ProfileTags = request.ProfileTags,
        Glob = request.Glob,
        HuntGlob = request.HuntGlob,
        HuntExclude = request.HuntExclude,
        SkipHunt = request.SkipHunt,
        SampleSize = request.SampleSize,
        OutputDir = request.OutputDir,
        CaptureId = request.CaptureId,
        Operator = request.Operator,
        Environment = request.Environment,
        Reason = request.Reason,
        MaskTokens = request.MaskTokens,
        Placeholder = request.Placeholder,
        AllowUnmasked = request.AllowUnmasked,
        RegistryScan = request.RegistryScans,
    };

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
