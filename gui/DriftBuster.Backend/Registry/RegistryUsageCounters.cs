using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Per-operation counters: calls, successes, errors, summed and last durations in seconds, last error, and first and last
/// invocation times.
/// </summary>
internal sealed class RegistryUsageCounters
{
    public long Calls { get; set; }

    public long Successes { get; set; }

    public long Errors { get; set; }

    public double TotalDuration { get; set; }

    public double? LastDuration { get; set; }

    public string? LastError { get; set; }

    public double? FirstInvocation { get; set; }

    public double? LastInvocation { get; set; }

    /// <summary>
    /// <c>operation</c>, <c>calls</c>, <c>successes</c>, <c>errors</c>, total/average (over successes)/last durations in milliseconds to
    /// three places, invocation times (<see cref="RegistryOperations.FormatTimestamp"/>) and <c>last_error</c>.
    /// </summary>
    public OrderedDictionary<string, object?> Snapshot(string name)
    {
        var average = Successes != 0 ? TotalDuration / Successes : 0.0;
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = name,
            ["calls"] = EngineValues.Narrow(Calls),
            ["successes"] = EngineValues.Narrow(Successes),
            ["errors"] = EngineValues.Narrow(Errors),
            ["total_duration_ms"] = IniPlugin.EngineRound(TotalDuration * 1000, 3),
            ["avg_duration_ms"] = IniPlugin.EngineRound(average * 1000, 3),
            ["last_duration_ms"] = IniPlugin.EngineRound((LastDuration ?? 0.0) * 1000, 3),
            ["first_invocation"] = RegistryOperations.FormatTimestamp(FirstInvocation),
            ["last_invocation"] = RegistryOperations.FormatTimestamp(LastInvocation),
            ["last_error"] = LastError,
        };
    }

    public void Reset()
    {
        Calls = 0;
        Successes = 0;
        Errors = 0;
        TotalDuration = 0.0;
        LastDuration = null;
        LastError = null;
        FirstInvocation = null;
        LastInvocation = null;
    }
}
