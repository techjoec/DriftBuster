using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry._UsageCounters</c>: calls, successes, errors, the summed and last durations in seconds, the last error text and the
/// first and last invocation times in seconds since the epoch.
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
    /// <c>counters.snapshot(name)</c>: <c>operation</c>, <c>calls</c>, <c>successes</c>, <c>errors</c>, the total, average (over
    /// successes, 0.0 without one) and last durations in milliseconds rounded to three places, the invocation times as
    /// <see cref="RegistryOperations.FormatTimestamp"/> spells them, and <c>last_error</c>.
    /// </summary>
    public OrderedDictionary<string, object?> Snapshot(string name)
    {
        var average = Successes != 0 ? TotalDuration / Successes : 0.0;
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = name,
            ["calls"] = PythonValues.Narrow(Calls),
            ["successes"] = PythonValues.Narrow(Successes),
            ["errors"] = PythonValues.Narrow(Errors),
            ["total_duration_ms"] = IniPlugin.PythonRound(TotalDuration * 1000, 3),
            ["avg_duration_ms"] = IniPlugin.PythonRound(average * 1000, 3),
            ["last_duration_ms"] = IniPlugin.PythonRound((LastDuration ?? 0.0) * 1000, 3),
            ["first_invocation"] = RegistryOperations.FormatTimestamp(FirstInvocation),
            ["last_invocation"] = RegistryOperations.FormatTimestamp(LastInvocation),
            ["last_error"] = LastError,
        };
    }

    /// <summary><c>counters.reset()</c>.</summary>
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
