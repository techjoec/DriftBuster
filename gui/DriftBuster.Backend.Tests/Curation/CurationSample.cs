using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Tests.Curation;

/// <summary>
/// Three servers (baseline, staging, prod) with two files: app.json where prod changes Cache.Minutes and a timestamp, has a
/// different password, and staging changes the timestamp; and legacy.json, which prod lacks.
/// </summary>
internal static class CurationSample
{
    public static readonly MultiServerPlan[] Plans =
    [
        new() { HostId = "a", Label = "baseline", Roots = ["/a"] },
        new() { HostId = "b", Label = "staging", Roots = ["/b"] },
        new() { HostId = "c", Label = "prod", Roots = ["/c"] },
    ];

    public static SettingsComparison Build()
    {
        var configs = new Dictionary<string, OrderedDictionary<string, ConfigRecord>>(StringComparer.Ordinal)
        {
            ["a"] = Host(Json("apps/web/app.json", """{"Cache": {"Minutes": 15}, "Built": "2026-01-01", "db": {"password": "one"}}"""), Json("legacy.json", """{"k": 1}""")),
            ["b"] = Host(Json("apps/web/app.json", """{"Cache": {"Minutes": 15}, "Built": "2026-02-02", "db": {"password": "one"}}"""), Json("legacy.json", """{"k": 1}""")),
            ["c"] = Host(Json("apps/web/app.json", """{"Cache": {"Minutes": 60}, "Built": "2026-03-03", "db": {"password": "two"}}""")),
        };
        return SettingsComparisonBuilder.Build(
            Plans,
            configs,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            Plans.Select(plan => new ServerScanResult { HostId = plan.HostId, Status = ServerScanStatus.Succeeded }).ToArray(),
            "a");
    }

    private static ConfigRecord Json(string path, string text) => new()
    {
        ConfigId = "json/" + path,
        DisplayName = path,
        FormatId = "json",
        ContentType = "json",
        Canonical = text,
        Raw = text,
        FileHash = "h",
        SourcePath = path,
        PluginName = "json",
        RelativePath = path,
    };

    private static OrderedDictionary<string, ConfigRecord> Host(params ConfigRecord[] records)
    {
        var host = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            host[record.ConfigId] = record;
        }

        return host;
    }
}
