using System.Diagnostics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <see cref="RegistryScan"/>'s operations with process-wide usage counters (calls, successes, errors, durations; a failure records
/// <c>"{type}: {message}"</c> and rethrows, a success clears the last error), and <see cref="RegistrySummary"/> over them.
/// </summary>
public static class RegistryOperations
{
    private const string EnumerateOperation = "enumerate_installed_apps";
    private const string FindRootsOperation = "find_app_registry_roots";
    private const string SearchOperation = "search_registry";

    private static readonly string[] Operations = [EnumerateOperation, FindRootsOperation, SearchOperation];

    private static readonly Dictionary<string, RegistryUsageCounters> Usage =
        Operations.ToDictionary(name => name, _ => new RegistryUsageCounters(), StringComparer.Ordinal);

    private static readonly Lock Gate = new();

    /// <summary>Instrumented <see cref="RegistryScan.EnumerateInstalledApps"/>.</summary>
    public static IReadOnlyList<RegistryApp> EnumerateInstalledApps(IRegistryBackend? backend = null)
        => Instrument(EnumerateOperation, () => RegistryScan.EnumerateInstalledApps(backend));

    /// <summary>Instrumented <see cref="RegistryScan.FindAppRegistryRoots"/>.</summary>
    public static IReadOnlyList<RegistryRoot> FindAppRegistryRoots(string appToken, IReadOnlyList<RegistryApp>? installed = null)
        => Instrument(FindRootsOperation, () => RegistryScan.FindAppRegistryRoots(appToken, installed));

    /// <summary>Instrumented <see cref="RegistryScan.SearchRegistry"/>.</summary>
    public static IReadOnlyList<RegistryHit> SearchRegistry(IEnumerable<RegistryRoot> roots, SearchSpec spec, IRegistryBackend? backend = null)
        => Instrument(SearchOperation, () => RegistryScan.SearchRegistry(roots, spec, backend));

    /// <summary>
    /// Instrumented search whose spec is built inside the instrumented call, so a limit that fails to convert counts as an error.
    /// </summary>
    internal static IReadOnlyList<RegistryHit> SearchRegistry(IEnumerable<RegistryRoot> roots, Func<SearchSpec> spec, IRegistryBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Instrument(SearchOperation, () => RegistryScan.SearchRegistry(roots, spec(), backend));
    }

    /// <summary>One counter snapshot per operation (enumerate, find roots, search); <paramref name="reset"/> clears them afterwards.</summary>
    public static IReadOnlyList<OrderedDictionary<string, object?>> RegistrySummary(bool reset = false)
    {
        lock (Gate)
        {
            var snapshot = Operations.Select(name => Usage[name].Snapshot(name)).ToList();
            if (reset)
            {
                foreach (var counters in Usage.Values)
                {
                    counters.Reset();
                }
            }

            return snapshot.AsReadOnly();
        }
    }

    /// <summary>
    /// Null for null, otherwise the Unix time as a UTC ISO 8601 timestamp ending "Z"; microseconds rounded ties-to-even, omitted when zero.
    /// </summary>
    public static string? FormatTimestamp(double? value)
    {
        if (value is not { } seconds)
        {
            return null;
        }

        var whole = Math.Floor(seconds);
        var microseconds = (long)Math.Round((seconds - whole) * 1e6, MidpointRounding.ToEven);
        var instant = DateTimeOffset.UnixEpoch.AddSeconds(whole).AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
        return IsoTimestamp.Format(instant).Replace("+00:00", "Z", StringComparison.Ordinal);
    }

    // Seconds since the Unix epoch.
    private static double WallClock() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    private static T Instrument<T>(string name, Func<T> operation)
    {
        var counters = Usage[name];
        lock (Gate)
        {
            counters.Calls++;
            var now = WallClock();
            counters.FirstInvocation ??= now;
            counters.LastInvocation = now;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = operation();
            var duration = Stopwatch.GetElapsedTime(start).TotalSeconds;
            lock (Gate)
            {
                counters.Successes++;
                counters.TotalDuration += duration;
                counters.LastDuration = duration;
                counters.LastError = null;
            }

            return result;
        }
        catch (Exception exc)
        {
            var duration = Stopwatch.GetElapsedTime(start).TotalSeconds;
            lock (Gate)
            {
                counters.Errors++;
                counters.TotalDuration += duration;
                counters.LastDuration = duration;
                counters.LastError = $"{exc.GetType().Name}: {exc.Message}";
            }

            throw;
        }
    }
}
