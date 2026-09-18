using System.Diagnostics;
using System.Globalization;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// Scans every host's roots in plan order, strictly one after another, and
/// builds the <c>multi-server.v2</c> response (host results, catalog, drilldown, summary and the settings comparison).
/// </summary>
/// <remarks>
/// Progress is reported on the calling thread through <see cref="IProgress{T}.Report"/>, throttled per run
/// (<see cref="ProgressThrottle"/>). <see cref="CancellationToken"/> is honoured per plan, per root, per file in the secret hunt
/// and the detector walk, per config and per host diff in the catalog, and during the throttle delay; cancellation surfaces as
/// <see cref="OperationCanceledException"/> and is never reported as a failed host. An unreadable file is skipped
/// and the host still succeeds; only a root that cannot be looked up, read or listed fails the host with
/// <c>permission_denied</c>.
/// </remarks>
public sealed partial class MultiServerRunner
{
    /// <summary><c>_DEFAULT_MULTI_SERVER_SAMPLE_BUDGET</c>: 64 MiB per host.</summary>
    public const long DefaultSampleBudget = 64L * 1024 * 1024;

    /// <summary>
    /// The largest file a record reads whole (<see cref="ReadText"/>): a sixth of the longest runtime string (0x3FFFFFDF
    /// characters). The text read always fits one string, and so, for ordinary text, do its canonical form and the cache entry's
    /// JSON; they are not bounded, though: indented canonical JSON grows with nesting and <c>json.dumps</c> escapes a control
    /// character as six characters, so a text near the limit can still pass the longest string, and the runtime's
    /// <see cref="OutOfMemoryException"/> then ends the run. A detected file past the limit is skipped as unreadable.
    /// </summary>
    internal const long DefaultMaxTextBytes = 0x3FFFFFDF / 6;

    /// <summary>Longest failure message, in code points, for a host whose scan raised unexpectedly.</summary>
    private const int MaxFailureMessageLength = 160;

    /// <param name="cacheDir">The diff cache directory; created when missing.</param>
    /// <param name="sampleBudget">Aggregate sampling budget per host; null uses <see cref="DefaultSampleBudget"/>.</param>
    /// <param name="sampleSize">Bytes sampled from each file; null uses <see cref="Detector.DefaultSampleSize"/>.</param>
    public MultiServerRunner(string cacheDir, long? sampleBudget = null, int? sampleSize = null)
    {
        ArgumentNullException.ThrowIfNull(cacheDir);
        Detector = new SkippingDetector(sampleSize, sampleBudget ?? DefaultSampleBudget);
        Cache = new DiffCache(cacheDir);
        ScanPlan = ScanPlanCore;
    }

    /// <summary>Seam for <c>runner._detector</c>; the default skips unreadable files (<see cref="SkippingDetector"/>).</summary>
    internal Detector Detector { get; set; }

    internal DiffCache Cache { get; }

    /// <summary>Test seam for <see cref="DefaultMaxTextBytes"/>.</summary>
    internal long MaxTextBytes { get; set; } = DefaultMaxTextBytes;

    /// <summary>Seam for <c>MultiServerRunner._scan_plan(plan, existing_roots, secret_hits)</c>.</summary>
    internal Func<MultiServerPlan, IReadOnlyList<string>, CancellationToken, PlanScan> ScanPlan { get; set; }

    /// <summary>Seam for <c>datetime.now(UTC)</c>.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Seam for <c>time.monotonic()</c>, in seconds.</summary>
    internal Func<double> Monotonic { get; set; } = static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Seam for the wait inside <c>time.sleep</c>, after <see cref="SleepDuration"/> accepted the argument; the default waits in
    /// whole milliseconds (rounded up, in steps the wait handle accepts), wakes early and raises when the token is cancelled.
    /// </summary>
    internal Action<TimeSpan, CancellationToken> Sleep { get; set; } = static (delay, token) =>
    {
        var step = TimeSpan.FromMilliseconds(int.MaxValue - 1);
        var remaining = TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));
        while (remaining > TimeSpan.Zero && !token.WaitHandle.WaitOne(remaining < step ? remaining : step))
        {
            remaining -= remaining < step ? remaining : step;
        }

        token.ThrowIfCancellationRequested();
    };

    /// <summary><c>MultiServerRunner.run(plans)</c>.</summary>
    public ServerScanResponse Run(IEnumerable<MultiServerPlan> plans, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var planList = plans.ToList();
        if (planList.Count == 0)
        {
            return BuildResponse([], [], [], string.Empty, new SettingsComparison());
        }

        var baselineHostId = BaselineSelector.Select(planList).HostId;
        var throttle = new ProgressThrottle();
        var hostResults = new List<ServerScanResult>();
        var hostConfigs = new OrderedDictionary<string, OrderedDictionary<string, ConfigRecord>>(StringComparer.Ordinal);
        var hostAvailability = new Dictionary<string, ServerAvailabilityStatus>(StringComparer.Ordinal);
        var hostUnreadable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var plan in planList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (result, configs, unreadable) = RunPlan(plan, throttle, progress, cancellationToken);
            hostResults.Add(result);
            hostConfigs[plan.HostId] = configs;
            hostAvailability[plan.HostId] = result.Availability;
            hostUnreadable[plan.HostId] = unreadable;
        }

        var (catalog, drilldown) = CatalogBuilder.Build(planList, hostConfigs, hostAvailability, baselineHostId, Now, cancellationToken);
        var comparison = SettingsComparisonBuilder.Build(planList, hostConfigs, hostUnreadable, hostResults, baselineHostId, cancellationToken);
        return BuildResponse(hostResults, catalog, drilldown, baselineHostId, comparison);
    }

    private (ServerScanResult Result, OrderedDictionary<string, ConfigRecord> Configs, IReadOnlyList<string> Unreadable) RunPlan(
        MultiServerPlan plan,
        ProgressThrottle throttle,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        void Emit(ServerScanStatus status, string message) => throttle.Report(progress, plan.HostId, status, message, Monotonic(), Now());

        Emit(ServerScanStatus.Running, $"Scanning {plan.Label}");
        var roots = plan.Roots.Select(LexicalPath.Str).ToList();
        var existingRoots = roots.Where(RootExists).ToList();
        if (existingRoots.Count == 0)
        {
            const string message = "No accessible roots.";
            Emit(ServerScanStatus.Failed, message);
            return (Result(plan, ServerScanStatus.Failed, ServerAvailabilityStatus.NotFound, message, roots), Empty(), []);
        }

        ServerScanResult result;
        var configs = Empty();
        IReadOnlyList<string> unreadable = [];
        try
        {
            var scan = ScanPlan(plan, existingRoots, cancellationToken);
            configs = scan.Configs;
            unreadable = RelativeToRoots(scan.SkippedFiles, existingRoots);
            var message = string.Create(CultureInfo.InvariantCulture, $"Evaluated {configs.Count} configuration(s).");
            if (scan.BudgetReached)
            {
                message += " Sample budget reached; additional files skipped.";
            }

            result = Result(plan, ServerScanStatus.Succeeded, ServerAvailabilityStatus.Found, message, existingRoots, scan.UsedCache, scan.BudgetReached);
        }
        catch (DetectorIOException error)
        {
            result = Result(plan, ServerScanStatus.Failed, ServerAvailabilityStatus.PermissionDenied, $"Permission denied: {error.Path}", roots);
        }
        catch (Exception exc) when (exc is not OperationCanceledException and not OutOfMemoryException)
        {
            result = Result(plan, ServerScanStatus.Failed, ServerAvailabilityStatus.Offline, TruncateCodePoints($"Scan failed: {exc.Message}", MaxFailureMessageLength), roots);
        }

        Emit(result.Status, result.Message);
        if (plan.ThrottleSeconds is { } seconds && seconds > 0)
        {
            Sleep(SleepDuration(seconds, Monotonic()), cancellationToken);
        }

        return result.Status == ServerScanStatus.Succeeded ? (result, configs, unreadable) : (result, Empty(), []);
    }

    // Skipped files as paths relative to the root that holds them, the form records use.
    private static string[] RelativeToRoots(IReadOnlyList<string> paths, IReadOnlyList<string> roots) =>
        paths.Select(path => roots.Select(root => LexicalPath.RelativeTo(EnginePath.Absolute(path), EnginePath.Absolute(root))).FirstOrDefault(relative => relative is not null) ?? PathText.Name(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static OrderedDictionary<string, ConfigRecord> Empty() => new(StringComparer.Ordinal);

    /// <summary>
    /// <c>time.sleep(seconds)</c>'s argument checks for a positive <paramref name="seconds"/>, which raise out of <c>run()</c>
    /// (the sleep follows the host's <c>try</c>): the timeout in nanoseconds, rounded up, must be below
    /// 2<sup>63</sup>, and off Windows the absolute deadline, CLOCK_MONOTONIC (<paramref name="monotonicSeconds"/>) plus the timeout,
    /// must be below 2<sup>63</sup> nanoseconds as well; either failure raises <see cref="OverflowException"/>.
    /// </summary>
    internal static TimeSpan SleepDuration(double seconds, double monotonicSeconds)
    {
        var nanoseconds = Math.Ceiling(seconds * 1e9);
        if (!(nanoseconds < 9223372036854775808.0))
        {
            throw new OverflowException("The throttle delay is too large.");
        }

        var timeout = (long)nanoseconds;
        if (!OperatingSystem.IsWindows() && (long)(monotonicSeconds * 1e9) > long.MaxValue - timeout)
        {
            throw new OverflowException("The throttle delay reaches past the end of the monotonic clock.");
        }

        return TimeSpan.FromTicks((timeout + 99) / 100);
    }

    // Path.exists(): a stat that follows links succeeds. A root whose lookup is refused counts as existing, so the scan reports
    // it as permission_denied. A root holding an unpaired surrogate names nothing the runtime can
    // open: it would encode it with U+FFFD and look up a different entry, so it is not found.
    private static bool RootExists(string root)
    {
        if (EngineUtf8.HasUnpairedSurrogate(root))
        {
            return false;
        }

        try
        {
            if (UnixFileType.Stat(root, followSymlinks: true) is { } kind)
            {
                return kind != UnixFileType.Kind.Missing;
            }

            return File.Exists(EnginePath.KernelPath(root)) || Directory.Exists(EnginePath.KernelPath(root));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary><c>datetime.now(UTC)</c> at microsecond precision.</summary>
    private DateTimeOffset Now()
    {
        var now = UtcNow().ToUniversalTime();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    private ServerScanResult Result(
        MultiServerPlan plan,
        ServerScanStatus status,
        ServerAvailabilityStatus availability,
        string message,
        IReadOnlyList<string> roots,
        bool usedCache = false,
        bool samplingGuardrailTriggered = false) => new()
        {
            HostId = plan.HostId,
            Label = plan.Label,
            Status = status,
            Message = message,
            Timestamp = Now(),
            Roots = roots.ToArray(),
            UsedCache = usedCache,
            Availability = availability,
            SamplingGuardrailTriggered = samplingGuardrailTriggered,
        };

    private ServerScanResponse BuildResponse(
        List<ServerScanResult> hostResults,
        ConfigCatalogEntry[] catalog,
        ConfigDrilldown[] drilldown,
        string baselineHostId,
        SettingsComparison comparison) => new()
        {
            Version = MultiServerSchema.Version,
            Comparison = comparison,
            Results = hostResults.ToArray(),
            Catalog = catalog,
            Drilldown = drilldown,
            Summary = new ServerScanSummary
            {
                BaselineHostId = baselineHostId,
                TotalHosts = hostResults.Count,
                ConfigsEvaluated = catalog.Length,
                DriftingConfigs = catalog.Count(entry => entry.DriftCount != 0),
                GeneratedAt = Now(),
            },
        };

    // text[:length] on code points.
    internal static string TruncateCodePoints(string text, int length)
    {
        var builder = new StringBuilder();
        var count = 0;
        var offset = 0;
        while (offset < text.Length && count < length)
        {
            var step = char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
            builder.Append(text, offset, step);
            offset += step;
            count++;
        }

        return builder.ToString();
    }
}
