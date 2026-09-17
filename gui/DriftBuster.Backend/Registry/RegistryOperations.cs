using System.Diagnostics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// The <c>driftbuster.registry</c> package surface: <see cref="RegistryScan"/>'s three operations wrapped by <c>_instrument</c>, which
/// counts every call, success and error with its duration (a failing call records <c>"{type}: {message}"</c> and rethrows, a
/// succeeding one clears the last error), and <see cref="RegistrySummary"/> over those counters. The counters are process-wide, as
/// the module-level <c>_USAGE</c> is.
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
    /// Instrumented <see cref="RegistryScan.SearchRegistry"/> over a spec built inside the instrumented call: Python's
    /// <c>search_registry</c> coerces <c>int(spec.max_depth)</c>, <c>int(spec.max_hits)</c> and <c>float(spec.time_budget_s)</c> itself,
    /// so a limit that does not convert counts as a call and an error; the typed <see cref="SearchSpec"/> holds the coerced values, and a
    /// caller holding raw ones hands their conversion in as <paramref name="spec"/>.
    /// </summary>
    internal static IReadOnlyList<RegistryHit> SearchRegistry(IEnumerable<RegistryRoot> roots, Func<SearchSpec> spec, IRegistryBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Instrument(SearchOperation, () => RegistryScan.SearchRegistry(roots, spec(), backend));
    }

    /// <summary>
    /// <c>registry_summary(reset=reset)</c>: one <see cref="RegistryUsageCounters.Snapshot"/> per operation in the order enumerate,
    /// find roots, search; with <paramref name="reset"/> every counter is cleared after the snapshot is taken.
    /// </summary>
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
    /// <c>_format_timestamp(value)</c>: null for null, otherwise <c>datetime.fromtimestamp(value, tz=UTC).isoformat()</c> with
    /// "+00:00" written as "Z". The fraction is rounded to microseconds half to even, as <c>_PyTime_ObjectToTimeval</c> rounds it,
    /// and left out when it is zero.
    /// </summary>
    public static string? FormatTimestamp(double? value)
    {
        if (value is not { } seconds)
        {
            return null;
        }

        var whole = Math.Truncate(seconds);
        var fraction = RoundHalfEven((seconds - whole) * 1e6);
        if (fraction >= 1e6)
        {
            fraction -= 1e6;
            whole += 1.0;
        }
        else if (fraction < 0)
        {
            fraction += 1e6;
            whole -= 1.0;
        }

        var instant = DateTime.UnixEpoch.AddSeconds(whole);
        var utc = PythonDateTime.Create(
            instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, instant.Second, (int)fraction, PythonFixedOffset.Utc);
        return utc.IsoFormat().Replace("+00:00", "Z", StringComparison.Ordinal);
    }

    // _PyTime_RoundHalfEven.
    private static double RoundHalfEven(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Abs(value - rounded) == 0.5 ? 2.0 * Math.Round(value / 2.0, MidpointRounding.AwayFromZero) : rounded;
    }

    // time.time(): seconds since the epoch.
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
                counters.LastError = $"{RegistryPython.ErrorName(exc)}: {exc.Message}";
            }

            throw;
        }
    }
}
