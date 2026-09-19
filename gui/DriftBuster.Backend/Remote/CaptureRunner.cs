using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Remote;

/// <summary>
/// The <c>capture</c> operations: <c>run</c> (detect and hunt a tree into a redacted snapshot plus a manifest), <c>compare</c> (two
/// snapshots) and <c>export-sql</c>. Progress lines go to stdout and refusals to stderr; each returns an exit code.
/// </summary>
public sealed class CaptureRunner(TimeProvider? time = null, Func<string>? hostName = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string> _hostName = hostName ?? CaptureHostName.Get;

    /// <summary>
    /// Refuses a missing root, a run without mask tokens (unless <see cref="CaptureRunOptions.AllowUnmasked"/>), and a missing operator
    /// (option, <c>DRIFTBUSTER_CAPTURE_OPERATOR</c>, <c>USER</c> or <c>USERNAME</c>), environment or reason; otherwise writes
    /// <c>&lt;id&gt;-snapshot.json</c> (every string redacted) and <c>&lt;id&gt;-manifest.json</c> under the output directory.
    /// </summary>
    public int Run(CaptureRunOptions options, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var root = Path.GetFullPath(options.Root);
        var refusal = Refusal(options, root, out var identity);
        if (refusal is not null)
        {
            stderr.Write($"error: {refusal}\n");
            return 1;
        }

        DetectionProfileStore? store = null;
        IReadOnlyList<RegistryScanSummary> registryScans;
        try
        {
            store = string.IsNullOrWhiteSpace(options.Profiles) ? null : DetectionProfileStore.Load(options.Profiles);
            registryScans = [.. options.RegistryScan.Select(SummariseRegistryScan)];
        }
        catch (Exception exc) when (exc is DetectionProfileException or IOException or JsonException or UnauthorizedAccessException)
        {
            stderr.Write($"error: {exc.Message}\n");
            return 1;
        }

        var capturedAt = _time.GetUtcNow();
        var id = string.IsNullOrEmpty(options.CaptureId) ? capturedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) : options.CaptureId;
        var info = identity with { Id = id, Root = root, CapturedAt = capturedAt, Host = _hostName() };
        var total = Stopwatch.StartNew();
        var watch = Stopwatch.StartNew();
        var detections = Detect(options, root, store, stderr);
        var detectionSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        IReadOnlyList<HuntHitResult> huntHits = options.SkipHunt
            ? []
            : [.. HuntEngine.HuntPath(root, HuntRules.Default, options.HuntGlob, options.SampleSize, options.HuntExclude).Hits.Select(hit => HuntHitResult.From(hit, root))];
        var huntSeconds = watch.Elapsed.TotalSeconds;

        var redactor = RedactionFilter.Resolve(maskTokens: options.MaskTokens, placeholder: options.Placeholder);
        var snapshot = Redacted(new CaptureSnapshot(info, detections, store?.Summary(), huntHits), redactor);

        var directory = Directory.CreateDirectory(options.OutputDir).FullName;
        var snapshotPath = Path.Join(directory, $"{id}-snapshot.json");
        var manifestPath = Path.Join(directory, $"{id}-manifest.json");
        AtomicFile.WriteAllText(snapshotPath, ModelJson.Serialize(snapshot));
        var redactions = redactor?.Stats().Values.Sum(count => (long)count) ?? 0;
        var manifest = Manifest(snapshot, (snapshotPath, manifestPath), (detectionSeconds, huntSeconds, total.Elapsed.TotalSeconds), new CaptureRedaction(options.Placeholder, options.MaskTokens.Count, redactions), registryScans);
        AtomicFile.WriteAllText(manifestPath, ModelJson.Serialize(manifest));
        stdout.Write($"Snapshot written to {snapshotPath}\nManifest written to {manifestPath}\n");
        if (redactor is not null && redactions == 0)
        {
            stderr.Write("warning: redaction filter configured but no tokens were replaced\n");
        }

        LastRun = (snapshotPath, manifestPath, manifest);
        return 0;
    }

    // Every string of the snapshot through the redactor.
    private static CaptureSnapshot Redacted(CaptureSnapshot snapshot, RedactionFilter? redactor)
        => redactor is null
            ? snapshot
            : redactor.ApplyTo(JsonSerializer.SerializeToNode(snapshot, ModelJson.TypeInfo<CaptureSnapshot>())).Deserialize(ModelJson.TypeInfo<CaptureSnapshot>())!;

    private static CaptureManifest Manifest(
        CaptureSnapshot snapshot,
        (string Snapshot, string Manifest) paths,
        (double Detection, double Hunt, double Total) seconds,
        CaptureRedaction redaction,
        IReadOnlyList<RegistryScanSummary> registryScans)
    {
        var captured = snapshot.Capture;
        return new CaptureManifest(
            CaptureManifest.CurrentSchemaVersion,
            new CaptureManifestInfo(captured.Id, Path.GetFileName(paths.Snapshot), Path.GetFileName(paths.Manifest), captured.CapturedAt, captured.Root, captured.Operator, captured.Environment, captured.Reason, captured.Host),
            new CaptureDurations(Math.Round(seconds.Detection, 3), Math.Round(seconds.Hunt, 3), Math.Round(seconds.Total, 3)),
            new CaptureCounts(
                snapshot.Detections.Count,
                snapshot.Detections.Sum(entry => entry.Profiles.Count),
                snapshot.HuntHits.Count,
                registryScans.Count,
                snapshot.ProfileSummary?.TotalProfiles ?? 0,
                snapshot.ProfileSummary?.TotalConfigs ?? 0),
            redaction,
            registryScans);
    }

    /// <summary>The paths and manifest of the last successful <see cref="Run"/>.</summary>
    public (string SnapshotPath, string ManifestPath, CaptureManifest Manifest)? LastRun { get; private set; }

    /// <summary>
    /// Compares two snapshot files and prints the summary. A missing current snapshot is an error; a missing baseline means this is the
    /// first capture.
    /// </summary>
    public int Compare(string baselinePath, string currentPath, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        LastComparison = null;
        if (!File.Exists(currentPath))
        {
            stderr.Write($"error: current snapshot not found: {currentPath}\n");
            return 1;
        }

        if (!File.Exists(baselinePath))
        {
            stdout.Write("No baseline snapshot found; record this run as the first capture.\n");
            return 0;
        }

        CaptureSnapshot baseline;
        CaptureSnapshot current;
        try
        {
            baseline = ReadSnapshot(baselinePath);
            current = ReadSnapshot(currentPath);
        }
        catch (DetectionProfileException exc)
        {
            stderr.Write($"error: {exc.Message}\n");
            return 1;
        }

        var comparison = CaptureComparer.Compare(baseline, current);
        CaptureComparer.Write(comparison, stdout);
        LastComparison = comparison;
        return 0;
    }

    /// <summary>The result of the last <see cref="Compare"/> that had both snapshots.</summary>
    public CaptureComparison? LastComparison { get; private set; }

    public static CaptureSnapshot ReadSnapshot(string path) => DetectionProfileStore.Read(path, ModelJson.TypeInfo<CaptureSnapshot>());

    /// <summary>
    /// Exports each database into <c>&lt;stem&gt;-sql-snapshot.json</c> under the output directory and, when any export succeeded, writes the
    /// manifest. A database that is missing or refused is reported on stderr and makes the exit code 1.
    /// </summary>
    public int ExportSql(SqlExportOptions options, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        LastExport = null;
        if (options.Settings.Limit is <= 0)
        {
            stderr.Write("error: --limit must be positive when provided\n");
            return 1;
        }

        var exports = new List<SqlExportEntry>();
        var written = new List<string>();
        var exitCode = 0;
        foreach (var database in options.Databases)
        {
            var path = Path.GetFullPath(RunProfileExpand(database));
            if (!File.Exists(path))
            {
                stderr.Write($"error: database not found: {path}\n");
                exitCode = 1;
                continue;
            }

            SqlSnapshot snapshot;
            try
            {
                snapshot = SqliteSnapshots.Build(path, options.Settings, _time);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exc)
            {
                stderr.Write($"error: failed to export {path}: {exc.Message}\n");
                exitCode = 1;
                continue;
            }

            var directory = Directory.CreateDirectory(options.OutputDir).FullName;
            var destination = SnapshotPath(directory, Stem(options, path));
            AtomicFile.WriteAllText(destination, ModelJson.Serialize(snapshot));
            exports.Add(new SqlExportEntry(path, Path.GetFileName(destination), "sqlite", snapshot.Tables.ToDictionary(table => table.Name, table => table.RowCount, StringComparer.Ordinal)));
            written.Add(destination);
            stdout.Write($"Exported SQL snapshot to {destination}\n");
        }

        if (exports.Count > 0)
        {
            var manifestPath = Path.Join(Path.GetFullPath(options.OutputDir), options.ManifestName);
            var manifest = new SqlExportManifest(_time.GetUtcNow(), exports, options.Settings);
            AtomicFile.WriteAllText(manifestPath, ModelJson.Serialize(manifest));
            if (options.ReportManifestPath)
            {
                stdout.Write($"Manifest written to {manifestPath}\n");
            }

            LastExport = (manifestPath, manifest, written);
        }

        return exitCode;
    }

    /// <summary>The manifest and snapshot paths of the last <see cref="ExportSql"/> that exported anything.</summary>
    public (string ManifestPath, SqlExportManifest Manifest, IReadOnlyList<string> SnapshotPaths)? LastExport { get; private set; }

    /// <summary>A registry scan output file: its token, the labels of its roots and requested roots, and its hit count.</summary>
    public static RegistryScanSummary SummariseRegistryScan(string path)
    {
        var full = Path.GetFullPath(RunProfileExpand(path));
        using var document = JsonDocument.Parse(File.ReadAllBytes(full));
        var root = document.RootElement;
        return new RegistryScanSummary(
            Path.GetFileName(full),
            full,
            root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null,
            RootLabels(root, "roots"),
            RootLabels(root, "requested_roots"),
            root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array ? hits.GetArrayLength() : 0);
    }

    private static string RunProfileExpand(string path) => Profiles.Run.RunProfileStore.Expand(path);

    // "HIVE \ path" for each root with both, " (view N)" when it names a view.
    private static List<string> RootLabels(JsonElement scan, string property)
    {
        var labels = new List<string>();
        if (!scan.TryGetProperty(property, out var roots) || roots.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }

        foreach (var entry in roots.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
        {
            var hive = Text(entry, "hive");
            var keyPath = Text(entry, "path");
            if (hive.Length > 0 && keyPath.Length > 0)
            {
                var view = Text(entry, "view");
                labels.Add($"{hive} \\ {keyPath}" + (view.Length > 0 ? $" (view {view})" : string.Empty));
            }
        }

        return labels;

        static string Text(JsonElement entry, string name)
            => entry.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? (value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()).Trim()
                : string.Empty;
    }

    private string? Refusal(CaptureRunOptions options, string root, out CaptureInfo identity)
    {
        identity = new CaptureInfo(string.Empty, root, default, string.Empty, string.Empty, string.Empty, string.Empty, options.Placeholder, options.MaskTokens.Count);
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            return $"capture root does not exist: {root}";
        }

        if (options.MaskTokens.Count == 0 && !options.AllowUnmasked)
        {
            return "provide at least one --mask-token or explicitly opt-in with --allow-unmasked";
        }

        var @operator = new[] { options.Operator, Environment.GetEnvironmentVariable("DRIFTBUSTER_CAPTURE_OPERATOR"), Environment.GetEnvironmentVariable("USER"), Environment.GetEnvironmentVariable("USERNAME") }
            .Select(candidate => candidate?.Trim()).FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate));
        if (@operator is null)
        {
            return "provide --operator or set DRIFTBUSTER_CAPTURE_OPERATOR/USER before running captures";
        }

        if (string.IsNullOrWhiteSpace(options.Environment))
        {
            return "--environment is required for capture manifests";
        }

        if (string.IsNullOrWhiteSpace(options.Reason))
        {
            return "--reason is required for capture manifests";
        }

        identity = identity with { Operator = @operator, Environment = options.Environment.Trim(), Reason = options.Reason.Trim() };
        return null;
    }

    private static List<CaptureDetection> Detect(CaptureRunOptions options, string root, DetectionProfileStore? store, TextWriter stderr)
    {
        var detector = new Detector(sampleSize: options.SampleSize, onWarning: message => stderr.Write(message + "\n"));
        var scanned = store is null
            ? detector.ScanPath(root, options.Glob).Select(result => new ProfiledDetection(result.Path, result.Match, []))
            : detector.ScanWithProfiles(root, store, options.ProfileTags, options.Glob);
        return [.. scanned.Where(entry => entry.Detection is not null).Select(entry => new CaptureDetection(
            entry.Path,
            Directory.Exists(root) ? Path.GetRelativePath(root, entry.Path).Replace('\\', '/') : Path.GetFileName(entry.Path),
            entry.Detection!.PluginName,
            entry.Detection.FormatName,
            entry.Detection.Variant,
            entry.Detection.Confidence,
            [.. entry.Detection.Reasons],
            (JsonObject)entry.Detection.Metadata.DeepClone(),
            [.. entry.Profiles.Select(applied => new CaptureProfileMatch(applied.Profile.Name, [.. applied.Profile.Tags.Order(StringComparer.Ordinal)], applied.Config))]))];
    }

    // The prefix or the database's stem; with several databases, "<prefix>-<stem>" (or the stem alone without a prefix).
    private static string Stem(SqlExportOptions options, string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return options.Databases.Count > 1
            ? string.IsNullOrEmpty(options.Prefix) ? stem : $"{options.Prefix}-{stem}"
            : string.IsNullOrEmpty(options.Prefix) ? stem : options.Prefix;
    }

    // "<stem>-sql-snapshot.json", then "-1", "-2", ... when that exists.
    private static string SnapshotPath(string directory, string stem)
    {
        var candidate = Path.Join(directory, $"{stem}-sql-snapshot.json");
        for (var counter = 1; File.Exists(candidate); counter++)
        {
            candidate = Path.Join(directory, string.Create(CultureInfo.InvariantCulture, $"{stem}-sql-snapshot-{counter}.json"));
        }

        return candidate;
    }
}
