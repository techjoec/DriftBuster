using System.Diagnostics;
using System.Globalization;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Remote;

/// <summary>
/// <c>driftbuster capture</c> as library calls: <see cref="RunCapture"/>, <see cref="RunSqlExport"/>, <see cref="CompareSnapshots"/>.
/// Each takes an options record and writes stdout/stderr text to the given writers. Clock, timer, host name and environment are seams.
/// </summary>
public static partial class CaptureRunner
{
    public const string CaptureManifestSchemaVersion = "1.0";

    /// <summary>UTC clock (test seam).</summary>
    internal static Func<DateTimeOffset> UtcNow { get; set; } = IsoTimestamp.UtcNow;

    /// <summary>Monotonic seconds (test seam).</summary>
    internal static Func<double> Monotonic { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Host name, domain included where the platform reports it (test seam).</summary>
    internal static Func<string> HostName { get; set; } = CaptureHostName.Get;

    /// <summary>Environment variable lookup (test seam).</summary>
    internal static Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Validates the root, the redaction opt-in, operator, environment and reason (each refusal written to <paramref name="stderr"/>
    /// as <c>error: ...</c>, exit 1); loads the optional profile store; scans with the detector (with profiles when given) and, unless
    /// skipped, the default hunt rules; summarises registry scan files; writes <c>{capture_id}-snapshot.json</c> (redacted) and
    /// <c>{capture_id}-manifest.json</c>; reports both paths and warns when a redaction filter replaced nothing.
    /// </summary>
    /// <remarks>Detector guardrail warnings go to <paramref name="stderr"/>. Unreadable files are skipped by the hunt.</remarks>
    public static CaptureRunOutcome RunCapture(CaptureRunOptions options, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (!TryValidateRun(options, stderr, out var root, out var identity))
        {
            return new CaptureRunOutcome(1);
        }

        DetectionProfileStore? profileStore = null;
        OrderedDictionary<string, object?>? profileSummary = null;
        if (!string.IsNullOrEmpty(options.Profiles))
        {
            try
            {
                profileStore = DetectionProfileCommands.StoreFromPayload(DetectionProfileCommands.LoadJson(options.Profiles));
                profileSummary = (OrderedDictionary<string, object?>)NormaliseSummary(profileStore.Summary())!;
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                stderr.Write($"error: failed to load profiles: {exc.Message}\n");
                return new CaptureRunOutcome(1);
            }
        }

        var detector = BuildDetector(options.SampleSize, stderr);
        var captureId = string.IsNullOrEmpty(options.CaptureId) ? CaptureTimestamp(UtcNow()) : options.CaptureId;
        var (snapshotPath, manifestPath) = PrepareOutputPaths(options.OutputDir, captureId);
        var scan = Scan(options, root, detector, profileStore);

        IReadOnlyList<OrderedDictionary<string, object?>> registryScans;
        try
        {
            registryScans = LoadRegistryScanSummaries(options.RegistryScan);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            stderr.Write($"error: {exc.Message}\n");
            return new CaptureRunOutcome(1);
        }

        return WriteCapture(options, identity with { CaptureId = captureId, Root = root }, scan, profileSummary, registryScans, (snapshotPath, manifestPath), stdout, stderr);
    }

    private sealed record CaptureIdentity(string CaptureId, string Root, string Operator, string Environment, string Reason);

    private sealed record CaptureScan(
        List<OrderedDictionary<string, object?>> Detections,
        List<OrderedDictionary<string, object?>> HuntHits,
        double DetectionDuration,
        double HuntDuration,
        double TotalDuration);

    // The checks made before anything is loaded or created; the first refusal goes to stderr.
    private static bool TryValidateRun(CaptureRunOptions options, TextWriter stderr, out string root, out CaptureIdentity identity)
    {
        root = EnginePath.Resolve(options.Root);
        identity = new CaptureIdentity(string.Empty, root, string.Empty, string.Empty, string.Empty);
        var refusal = FirstRefusal(options, root, out var @operator, out var environment, out var reason);
        if (refusal is not null)
        {
            stderr.Write(refusal + "\n");
            return false;
        }

        identity = identity with { Operator = @operator, Environment = environment, Reason = reason };
        return true;
    }

    private static string? FirstRefusal(CaptureRunOptions options, string root, out string @operator, out string environment, out string reason)
    {
        @operator = environment = reason = string.Empty;
        if (!RunProfileStore.Exists(root))
        {
            return $"error: capture root does not exist: {root}";
        }

        if (options.MaskTokens.Count == 0 && !options.AllowUnmasked)
        {
            return "error: provide at least one --mask-token or explicitly opt-in with --allow-unmasked";
        }

        if (ResolveOperator(options.Operator) is not { } resolved)
        {
            return "error: provide --operator or set DRIFTBUSTER_CAPTURE_OPERATOR/USER before running captures";
        }

        @operator = resolved;
        environment = EngineText.Strip(options.Environment ?? string.Empty);
        if (environment.Length == 0)
        {
            return "error: --environment is required for capture manifests";
        }

        reason = EngineText.Strip(options.Reason ?? string.Empty);
        return reason.Length == 0 ? "error: --reason is required for capture manifests" : null;
    }

    // A detector whose guardrail warnings go to stderr; a size past its int parameter is clamped here with the detector's warning text.
    private static Detector BuildDetector(long sampleSize, TextWriter stderr)
    {
        void Warn(string message) => stderr.Write(message + "\n");
        if (sampleSize > int.MaxValue)
        {
            Warn(string.Create(CultureInfo.InvariantCulture, $"Sample size {sampleSize} exceeds {Detector.MaxSampleSize} bytes; clamping to guardrail."));
            return new Detector(sampleSize: Detector.MaxSampleSize, onWarning: Warn);
        }

        return new Detector(sampleSize: (int)Math.Max(sampleSize, int.MinValue), onWarning: Warn);
    }

    // Detection scan, then hunt, each timed.
    private static CaptureScan Scan(CaptureRunOptions options, string root, Detector detector, DetectionProfileStore? profileStore)
    {
        var startTime = Monotonic();
        var detectionStart = Monotonic();
        List<OrderedDictionary<string, object?>> detections = profileStore is not null
            ? detector.ScanWithProfiles(root, profileStore, options.ProfileTags, options.Glob)
                .Where(entry => entry.Detection is not null)
                .Select(entry => SerialiseDetection(entry, root))
                .ToList()
            : detector.ScanPath(root, options.Glob)
                .Where(result => result.Match is not null)
                .Select(result => SerialisePlainDetection(result.Path, result.Match!, root))
                .ToList();
        var detectionDuration = Monotonic() - detectionStart;

        var huntHits = new List<OrderedDictionary<string, object?>>();
        var huntDuration = 0.0;
        if (!options.SkipHunt)
        {
            var huntStart = Monotonic();
            var hits = HuntEngine.HuntPath(root, HuntRules.Default, options.HuntGlob, options.SampleSize, options.HuntExclude);
            huntDuration = Monotonic() - huntStart;
            huntHits = hits.Hits.Select(hit => SerialiseHuntHit(hit, root)).ToList();
        }

        var totalDuration = Monotonic() - startTime;
        return new CaptureScan(detections, huntHits, detectionDuration, huntDuration, totalDuration);
    }

    private static CaptureRunOutcome WriteCapture(
        CaptureRunOptions options,
        CaptureIdentity identity,
        CaptureScan scan,
        OrderedDictionary<string, object?>? profileSummary,
        IReadOnlyList<OrderedDictionary<string, object?>> registryScans,
        (string Snapshot, string Manifest) paths,
        TextWriter stdout,
        TextWriter stderr)
    {
        var redactor = RedactionFilter.Resolve(maskTokens: options.MaskTokens, placeholder: options.Placeholder);
        var snapshotPayload = BuildSnapshotPayload(identity, options.Placeholder, options.MaskTokens, scan.Detections, profileSummary, scan.HuntHits);
        var redactedSnapshot = redactor is null ? snapshotPayload : (OrderedDictionary<string, object?>)RedactionFilter.RedactData(snapshotPayload, redactor)!;
        WriteJsonText(paths.Snapshot, redactedSnapshot);

        var profileMatchCount = scan.Detections.Sum(entry => EngineBuiltins.Len(entry.GetValueOrDefault("profiles")));
        var totalRedactions = redactor?.Stats().Values.Sum(count => (long)count) ?? 0;
        var manifestPayload = BuildManifestPayload(
            (IReadOnlyDictionary<string, object?>)redactedSnapshot["capture"]!,
            paths.Snapshot,
            paths.Manifest,
            scan.DetectionDuration,
            scan.HuntDuration,
            scan.TotalDuration,
            scan.Detections.Count,
            profileMatchCount,
            scan.HuntHits.Count,
            profileSummary,
            options.Placeholder,
            options.MaskTokens.Count,
            totalRedactions,
            registryScans);
        WriteJsonText(paths.Manifest, manifestPayload);

        stdout.Write($"Snapshot written to {paths.Snapshot}\nManifest written to {paths.Manifest}\n");
        if (redactor is not null && totalRedactions == 0)
        {
            stderr.Write("warning: redaction filter configured but no tokens were replaced\n");
        }

        return new CaptureRunOutcome(0, paths.Snapshot, paths.Manifest, redactedSnapshot, manifestPayload);
    }

    /// <summary>
    /// The first non-blank of <paramref name="value"/>, <c>DRIFTBUSTER_CAPTURE_OPERATOR</c>, <c>USER</c>, <c>USERNAME</c>, trimmed; null
    /// when all are blank.
    /// </summary>
    public static string? ResolveOperator(string? value)
    {
        string?[] candidates =
        [
            value,
            GetEnvironmentVariable("DRIFTBUSTER_CAPTURE_OPERATOR"),
            GetEnvironmentVariable("USER"),
            GetEnvironmentVariable("USERNAME"),
        ];
        return candidates.Select(candidate => EngineText.Strip(candidate ?? string.Empty)).FirstOrDefault(candidate => candidate.Length > 0);
    }

    /// <summary>A capture id from the UTC clock: <c>yyyyMMddTHHmmssZ</c>.</summary>
    internal static string CaptureTimestamp(DateTimeOffset now)
        => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Creates the directory and returns the snapshot and manifest paths under it.</summary>
    public static (string SnapshotPath, string ManifestPath) PrepareOutputPaths(string directory, string captureId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(captureId);
        EnginePath.MakeDirectories(directory);
        return (LexicalPath.Join(directory, $"{captureId}-snapshot.json"), LexicalPath.Join(directory, $"{captureId}-manifest.json"));
    }
}
