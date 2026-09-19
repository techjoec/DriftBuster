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
    /// <summary>Aggregate sampling budget per host.</summary>
    public const long DefaultSampleBudget = 64L * 1024 * 1024;

    /// <summary>
    /// The largest file read whole: a sixth of the longest runtime string. Canonical forms and cache JSON can still grow past it
    /// (escaping, indentation), in which case the runtime's <see cref="OutOfMemoryException"/> ends the run. Larger detected files are
    /// skipped as unreadable.
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

    /// <summary>The detector (test seam); the default skips unreadable files (<see cref="SkippingDetector"/>).</summary>
    internal Detector Detector { get; set; }

    internal DiffCache Cache { get; }

    /// <summary>Test seam for <see cref="DefaultMaxTextBytes"/>.</summary>
    internal long MaxTextBytes { get; set; } = DefaultMaxTextBytes;

    /// <summary>The per-host scan (test seam).</summary>
    internal Func<MultiServerPlan, IReadOnlyList<string>, CancellationToken, PlanScan> ScanPlan { get; set; }

    /// <summary>UTC clock (test seam).</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Monotonic seconds (test seam).</summary>
    internal Func<double> Monotonic { get; set; } = static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// The throttle wait (test seam); the default waits in whole milliseconds, rounded up, and throws when the token is cancelled.
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
        if (existingRoots.Count == 0 && plan.Registry is null)
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
            var scan = existingRoots.Count > 0 ? ScanPlan(plan, existingRoots, cancellationToken) : new PlanScan(Empty(), UsedCache: false, BudgetReached: false, []);
            configs = scan.Configs;
            unreadable = RelativeToRoots(scan.SkippedFiles, existingRoots);
            var registry = plan.Registry is null ? null : ScanRegistry(plan, configs, cancellationToken);
            var message = string.Create(CultureInfo.InvariantCulture, $"Evaluated {configs.Count} configuration(s).");
            if (scan.BudgetReached)
            {
                message += " Sample budget reached; additional files skipped.";
            }

            message += registry?.Message ?? string.Empty;
            result = existingRoots.Count == 0 && registry is { Failed: true }
                ? Result(plan, ServerScanStatus.Failed, ServerAvailabilityStatus.Offline, message.TrimStart(), roots)
                : Result(plan, ServerScanStatus.Succeeded, ServerAvailabilityStatus.Found, message, existingRoots, scan.UsedCache && registry is null, scan.BudgetReached);
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
    /// The throttle delay: the timeout in nanoseconds (rounded up) must be below 2<sup>63</sup>, and off Windows so must the monotonic
    /// deadline; otherwise <see cref="OverflowException"/>, which escapes <c>Run</c>.
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

    // Exists after following links. A refused lookup counts as existing, so the scan reports permission_denied. A root with an unpaired
    // surrogate is not found, since the runtime would look up its U+FFFD spelling.
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

    /// <summary>UTC now at microsecond precision.</summary>
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

    // The first length code points.
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
