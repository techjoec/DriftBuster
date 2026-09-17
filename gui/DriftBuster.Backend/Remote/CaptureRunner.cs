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
/// <c>scripts/capture.py</c> as library calls: <c>run</c> (<see cref="RunCapture"/>), <c>export-sql</c> (<see cref="RunSqlExport"/>) and
/// <c>compare</c> (<see cref="CompareSnapshots"/>). Each takes the command's arguments as an options record and writes what the script
/// writes to <c>sys.stdout</c> and <c>sys.stderr</c> to the writers it is given; an exception the script lets escape escapes here too.
/// The clock, the monotonic timer, the host name and the environment are settable seams read exactly where the script reads them.
/// </summary>
public static partial class CaptureRunner
{
    /// <summary><c>CAPTURE_MANIFEST_SCHEMA_VERSION</c>.</summary>
    public const string CaptureManifestSchemaVersion = "1.0";

    /// <summary><c>datetime.now(UTC)</c>.</summary>
    internal static Func<PythonDateTime> UtcNow { get; set; } = PythonDateTime.UtcNow;

    /// <summary><c>time.monotonic()</c> in seconds.</summary>
    internal static Func<double> Monotonic { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary><c>socket.gethostname()</c> (<see cref="CaptureHostName"/>): the host name as the platform reports it, domain part included.</summary>
    internal static Func<string> HostName { get; set; } = CaptureHostName.Get;

    /// <summary><c>os.getenv(name)</c>.</summary>
    internal static Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// <c>run_capture(args)</c>: validates the root, the redaction opt-in, the operator, environment and reason (each refusal written to
    /// <paramref name="stderr"/> as <c>error: ...</c> with exit code 1), loads the optional profile store, scans the root with the detector
    /// (with profiles when a store is given) and, unless skipped, with the default hunt rules, summarises the registry scan files, then writes
    /// <c>{capture_id}-snapshot.json</c> (redacted) and <c>{capture_id}-manifest.json</c> as <c>json.dumps(..., indent=2, sort_keys=True)</c>
    /// under the output directory, reports both paths on <paramref name="stdout"/> and warns when a redaction filter replaced nothing.
    /// </summary>
    /// <remarks>
    /// Detector guardrail warnings go to <paramref name="stderr"/> as Python's last-resort logging handler writes them. Fix b: a file the
    /// hunt cannot read is skipped where Python's hunt aborts the capture. Directory creation and file writes raise Python's
    /// <c>OSError</c> text (<see cref="PythonOSError"/>).
    /// </remarks>
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

    // The checks run_capture makes before anything is loaded or created, in its order; the first refusal is written to stderr.
    private static bool TryValidateRun(CaptureRunOptions options, TextWriter stderr, out string root, out CaptureIdentity identity)
    {
        root = PythonPath.Resolve(options.Root);
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
        environment = PythonText.Strip(options.Environment ?? string.Empty);
        if (environment.Length == 0)
        {
            return "error: --environment is required for capture manifests";
        }

        reason = PythonText.Strip(options.Reason ?? string.Empty);
        return reason.Length == 0 ? "error: --reason is required for capture manifests" : null;
    }

    // Detector(sample_size=args.sample_size), its guardrail warnings written as logging's last-resort handler writes them. A size past the
    // detector's int parameter is clamped here with the detector's own warning text, as the detector clamps any size past its guardrail.
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

    // The detection scan, then the hunt, timed with time.monotonic() at the points run_capture reads it.
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

        var profileMatchCount = scan.Detections.Sum(entry => PythonBuiltins.Len(entry.GetValueOrDefault("profiles")));
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
    /// <c>_resolve_operator(value)</c>: the first of <paramref name="value"/>, <c>DRIFTBUSTER_CAPTURE_OPERATOR</c>, <c>USER</c> and
    /// <c>USERNAME</c> that is not blank, stripped; null when all are blank. The three variables are read before any is tested.
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
        return candidates.Select(candidate => PythonText.Strip(candidate ?? string.Empty)).FirstOrDefault(candidate => candidate.Length > 0);
    }

    /// <summary>
    /// <c>datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")</c> for a capture identifier. <c>%Y</c> is the year as glibc's <c>strftime</c>
    /// writes it, without padding (year 999 is <c>999</c>), on every platform; a clock never reads below the year 1000.
    /// </summary>
    internal static string CaptureTimestamp(PythonDateTime now)
        => string.Create(CultureInfo.InvariantCulture, $"{now.Year}{now.Month:D2}{now.Day:D2}T{now.Hour:D2}{now.Minute:D2}{now.Second:D2}Z");

    /// <summary>
    /// <c>_prepare_output_paths(directory, capture_id)</c>: creates the directory (<c>mkdir(parents=True, exist_ok=True)</c>) and returns
    /// <c>{capture_id}-snapshot.json</c> and <c>{capture_id}-manifest.json</c> under it, as <c>str(directory / name)</c> spells them.
    /// </summary>
    public static (string SnapshotPath, string ManifestPath) PrepareOutputPaths(string directory, string captureId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(captureId);
        PythonPath.MakeDirectories(directory);
        return (PythonPurePath.Join(directory, $"{captureId}-snapshot.json"), PythonPurePath.Join(directory, $"{captureId}-manifest.json"));
    }
}
