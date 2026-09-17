using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

internal static partial class SelfcheckMultiServerPaths
{
    private static OrderedDictionary<string, object?> Map() => new(StringComparer.Ordinal);

    private static object? LastEvent(List<object?> events, string eventType)
        => Enumerable.Reverse(events).FirstOrDefault(item => EngineBuiltins.Get(item, "type") is string type && string.Equals(type, eventType, StringComparison.Ordinal));

    /// <summary><c>evaluate_response(name, returncode, events, stderr)</c>.</summary>
    public static OrderedDictionary<string, object?> EvaluateResponse(string name, int returnCode, List<object?> events, string stderr)
    {
        var resultEvent = LastEvent(events, "result");
        var errorEvent = LastEvent(events, "error");
        object? payload = resultEvent is IReadOnlyDictionary<string, object?> result
            ? result.TryGetValue("payload", out var value) ? value : Map()
            : Map();
        var mapping = payload as IReadOnlyDictionary<string, object?>;
        object? Field(string key) => mapping is null ? new List<object?>() : mapping.TryGetValue(key, out var item) ? item : new List<object?>();
        var results = Field("results");
        var catalog = Field("catalog");
        var drilldown = Field("drilldown");
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["returncode"] = (long)returnCode,
            ["event_count"] = (long)events.Count,
            ["has_result"] = resultEvent is not null,
            ["error_message"] = errorEvent is IReadOnlyDictionary<string, object?> ? EngineBuiltins.Get(errorEvent, "message") : null,
            ["stderr"] = stderr,
            ["hosts"] = ListLength(results),
            ["catalog"] = ListLength(catalog),
            ["drilldown"] = ListLength(drilldown),
            ["failed_hosts"] = CountEntries(results, entry => EngineBuiltins.Get(entry, "status") is "failed"),
            ["drift_entries"] = CountEntries(catalog, entry => EngineBuiltins.Int(DriftCount(entry)) > BigInteger.Zero),
            ["payload"] = payload,
        };
    }

    private static object? DriftCount(IReadOnlyDictionary<string, object?> entry) => entry.TryGetValue("drift_count", out var count) ? count : 0L;

    private static long ListLength(object? value) => value is List<object?> list ? list.Count : 0;

    private static long CountEntries(object? value, Func<IReadOnlyDictionary<string, object?>, bool> predicate)
        => value is List<object?> list ? list.OfType<IReadOnlyDictionary<string, object?>>().LongCount(predicate) : 0;

    /// <summary><c>make_plan(host_id, label, root, preferred=..., priority=..., scope=...)</c>.</summary>
    public static OrderedDictionary<string, object?> MakePlan(string hostId, string label, string root, bool preferred, long priority, string scope = "custom_roots")
        => new(StringComparer.Ordinal)
        {
            ["host_id"] = hostId,
            ["label"] = label,
            ["scope"] = scope,
            ["roots"] = new List<object?> { LexicalPath.Str(root) },
            ["baseline"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["is_preferred"] = preferred, ["priority"] = priority },
        };

    private static OrderedDictionary<string, object?> Request(string cacheDir, params OrderedDictionary<string, object?>[] plans)
        => new(StringComparer.Ordinal)
        {
            ["schema_version"] = "multi-server.v1",
            ["cache_dir"] = cacheDir,
            ["plans"] = plans.Cast<object?>().ToList(),
        };

    private static long Number(OrderedDictionary<string, object?> details, string key) => (long)details[key]!;

    private static bool Is(OrderedDictionary<string, object?> details, string key) => (bool)details[key]!;
}
