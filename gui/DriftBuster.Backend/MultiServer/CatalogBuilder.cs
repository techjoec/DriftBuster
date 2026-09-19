using System.Text.Json;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary>One catalog entry and one drilldown per config id.</summary>
public static class CatalogBuilder
{
    private sealed record HostDiff(string Before, string After, string Diff, DiffResultSummary Summary);

    private sealed record ConfigView(
        string ConfigId,
        OrderedDictionary<string, ConfigRecord> PerHost,
        ConfigRecord Baseline,
        List<string> PresentHostIds,
        Dictionary<string, int> DriftStats,
        OrderedDictionary<string, HostDiff> UnifiedDiffs);

    /// <summary>
    /// Config ids in code-point order; the baseline record is the baseline host's, else the code-point-smallest host holding the config.
    /// Every present host (plan order) is diffed against the baseline's canonical payload; <c>drift_count</c> counts hosts with changes;
    /// severity is <c>high</c> at max(1, hosts / 2) drifting hosts, <c>medium</c> for any, else <c>none</c>.
    /// </summary>
    /// <param name="hostConfigs">Each host's records by config id, hosts in scan order.</param>
    public static (ConfigCatalogEntry[] Catalog, ConfigDrilldown[] Drilldown) Build(
        IReadOnlyList<MultiServerPlan> plans,
        OrderedDictionary<string, OrderedDictionary<string, ConfigRecord>> hostConfigs,
        IReadOnlyDictionary<string, ServerAvailabilityStatus> hostAvailability,
        string baselineHostId,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(hostConfigs);
        ArgumentNullException.ThrowIfNull(hostAvailability);
        ArgumentNullException.ThrowIfNull(utcNow);
        var configIndex = new Dictionary<string, OrderedDictionary<string, ConfigRecord>>(StringComparer.Ordinal);
        foreach (var (hostId, configs) in hostConfigs)
        {
            foreach (var (configId, record) in configs)
            {
                if (!configIndex.TryGetValue(configId, out var perHost))
                {
                    perHost = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal);
                    configIndex[configId] = perHost;
                }

                perHost[hostId] = record;
            }
        }

        // A later plan with the same host id replaces an earlier one.
        var hostsById = new Dictionary<string, MultiServerPlan>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            hostsById[plan.HostId] = plan;
        }

        var catalog = new List<ConfigCatalogEntry>();
        var drilldown = new List<ConfigDrilldown>();
        var configIds = configIndex.Keys.ToList();
        configIds.Sort(StringComparer.Ordinal);
        foreach (var configId in configIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = BuildView(plans, hostsById, configId, configIndex[configId], baselineHostId, cancellationToken);
            catalog.Add(CatalogEntry(plans, hostsById, view, utcNow));
            drilldown.Add(DrilldownEntry(plans, hostsById, hostAvailability, view, baselineHostId, utcNow));
        }

        return (catalog.ToArray(), drilldown.ToArray());
    }

    private static ConfigView BuildView(
        IReadOnlyList<MultiServerPlan> plans,
        Dictionary<string, MultiServerPlan> hostsById,
        string configId,
        OrderedDictionary<string, ConfigRecord> perHost,
        string baselineHostId,
        CancellationToken cancellationToken)
    {
        if (!perHost.TryGetValue(baselineHostId, out var baseline))
        {
            var fallbackHostId = perHost.Keys.Min(StringComparer.Ordinal)!;
            baseline = perHost[fallbackHostId];
        }

        var presentHostIds = plans.Select(plan => plan.HostId).Where(perHost.ContainsKey).ToList();
        var driftStats = new Dictionary<string, int>(StringComparer.Ordinal);
        var unifiedDiffs = new OrderedDictionary<string, HostDiff>(StringComparer.Ordinal);
        var baselineLabel = $"{hostsById[baselineHostId].Label}:{configId}";
        foreach (var plan in plans)
        {
            if (!perHost.TryGetValue(plan.HostId, out var record))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var hostLabel = $"{hostsById[plan.HostId].Label}:{configId}";
            var diff = DiffBuilder.BuildUnifiedDiff(baseline.Canonical, record.Canonical, baseline.ContentType, baselineLabel, hostLabel);
            driftStats[plan.HostId] = diff.Stats.AddedLines + diff.Stats.RemovedLines + diff.Stats.ChangedLines;
            var summary = DiffBuilder.SummariseDiffResult(diff, [baselineLabel, hostLabel], baseline.DisplayName, record.DisplayName);
            unifiedDiffs[plan.HostId] = new HostDiff(baseline.Raw, record.Raw, diff.Diff, summary);
        }

        return new ConfigView(configId, perHost, baseline, presentHostIds, driftStats, unifiedDiffs);
    }

    /// <summary><c>high</c> when <paramref name="driftCount"/> reaches max(1, totalHosts / 2) (integer division).</summary>
    public static string Severity(int driftCount, int totalHosts)
    {
        if (driftCount >= Math.Max(1, totalHosts / 2))
        {
            return "high";
        }

        return driftCount > 0 ? "medium" : "none";
    }

    private static int DriftCount(ConfigView view) => view.DriftStats.Values.Count(value => value > 0);

    private static ConfigCatalogEntry CatalogEntry(
        IReadOnlyList<MultiServerPlan> plans,
        Dictionary<string, MultiServerPlan> hostsById,
        ConfigView view,
        Func<DateTimeOffset> utcNow)
    {
        var driftCount = DriftCount(view);
        var coverage = "full";
        if (view.PresentHostIds.Count == 0)
        {
            coverage = "missing";
        }
        else if (view.PresentHostIds.Count != plans.Count)
        {
            coverage = "partial";
        }

        var missingLabels = MissingLabels(plans, hostsById, view);
        return new ConfigCatalogEntry
        {
            ConfigId = view.ConfigId,
            DisplayName = view.Baseline.DisplayName,
            Format = view.Baseline.FormatId,
            DriftCount = driftCount,
            Severity = Severity(driftCount, plans.Count),
            PresentHosts = view.PresentHostIds.Select(hostId => hostsById[hostId].Label).ToArray(),
            MissingHosts = missingLabels,
            LastUpdated = utcNow(),
            HasSecrets = view.PerHost.Values.Any(record => record.Secrets),
            HasMaskedTokens = view.PerHost.Values.Any(record => record.Masked),
            HasValidationIssues = missingLabels.Length > 0,
            CoverageStatus = coverage,
        };
    }

    private static string[] MissingLabels(IReadOnlyList<MultiServerPlan> plans, Dictionary<string, MultiServerPlan> hostsById, ConfigView view)
        => plans.Where(plan => !view.PerHost.ContainsKey(plan.HostId)).Select(plan => hostsById[plan.HostId].Label).ToArray();

    private static ConfigDrilldown DrilldownEntry(
        IReadOnlyList<MultiServerPlan> plans,
        Dictionary<string, MultiServerPlan> hostsById,
        IReadOnlyDictionary<string, ServerAvailabilityStatus> hostAvailability,
        ConfigView view,
        string baselineHostId,
        Func<DateTimeOffset> utcNow)
    {
        var servers = plans
            .Select(plan => ServerDetail(hostsById[plan.HostId], view, hostAvailability, baselineHostId, utcNow))
            .ToArray();
        var chosen = ChooseDiff(view, baselineHostId);
        var chosenHostId = chosen is null ? string.Empty
            : view.UnifiedDiffs.FirstOrDefault(pair => ReferenceEquals(pair.Value, chosen)).Key ?? string.Empty;
        var plugin = view.Baseline.PluginName;
        return new ConfigDrilldown
        {
            ConfigId = view.ConfigId,
            DisplayName = view.Baseline.DisplayName,
            Format = view.Baseline.FormatId,
            Servers = servers,
            BaselineHostId = baselineHostId,
            DiffBefore = chosen?.Before ?? view.Baseline.Raw,
            DiffAfter = chosen?.After ?? view.Baseline.Raw,
            UnifiedDiff = chosen?.Diff ?? string.Empty,
            DiffHostId = chosenHostId,
            HostDiffs = view.UnifiedDiffs
                .Where(pair => !string.Equals(pair.Key, baselineHostId, StringComparison.Ordinal))
                .Select(pair => new ConfigHostDiff { HostId = pair.Key, After = pair.Value.After, UnifiedDiff = pair.Value.Diff })
                .ToArray(),
            DiffSummary = chosen?.Summary,
            HasSecrets = view.PerHost.Values.Any(record => record.Secrets),
            HasMaskedTokens = view.PerHost.Values.Any(record => record.Masked),
            HasValidationIssues = MissingLabels(plans, hostsById, view).Length > 0,
            Notes = [$"Detected via {plugin}"],
            Provenance = $"detector:{plugin}",
            DriftCount = DriftCount(view),
            LastUpdated = utcNow(),
        };
    }

    // The single diff pane: the first other host whose diff against the baseline has changes, so the pane shows drift when
    // there is any; else the baseline host's own (empty) diff, else the first diff recorded; null stands for an empty pane
    // over the baseline's raw text.
    private static HostDiff? ChooseDiff(ConfigView view, string baselineHostId)
    {
        foreach (var (hostId, diff) in view.UnifiedDiffs)
        {
            if (!string.Equals(hostId, baselineHostId, StringComparison.Ordinal) && diff.Diff.Length > 0)
            {
                return diff;
            }
        }

        if (view.UnifiedDiffs.TryGetValue(baselineHostId, out var own))
        {
            return own;
        }

        return view.UnifiedDiffs.Count > 0 ? view.UnifiedDiffs.GetAt(0).Value : null;
    }

    private static ConfigServerDetail ServerDetail(
        MultiServerPlan plan,
        ConfigView view,
        IReadOnlyDictionary<string, ServerAvailabilityStatus> hostAvailability,
        string baselineHostId,
        Func<DateTimeOffset> utcNow)
    {
        view.PerHost.TryGetValue(plan.HostId, out var record);
        var availability = hostAvailability.TryGetValue(plan.HostId, out var known) ? known : ServerAvailabilityStatus.NotFound;
        var present = record is not null && availability == ServerAvailabilityStatus.Found;
        var driftLines = present && view.DriftStats.TryGetValue(plan.HostId, out var lines) ? lines : 0;
        var secrets = record?.Secrets ?? false;
        var masked = record?.Masked ?? false;
        var status = availability switch
        {
            _ when present => driftLines > 0 ? "Drift" : "Match",
            ServerAvailabilityStatus.PermissionDenied => "Permission denied",
            ServerAvailabilityStatus.Offline => "Offline",
            _ => "Missing",
        };

        return new ConfigServerDetail
        {
            HostId = plan.HostId,
            Label = plan.Label,
            Present = present,
            IsBaseline = string.Equals(plan.HostId, baselineHostId, StringComparison.Ordinal),
            Status = status,
            DriftLineCount = driftLines,
            HasSecrets = secrets,
            Masked = masked,
            RedactionStatus = masked ? "Masked" : secrets ? "Secrets" : "Visible",
            LastSeen = utcNow(),
            PresenceStatus = present ? ConfigPresenceStatus.Found : ToPresence(availability),
        };
    }

    private static ConfigPresenceStatus ToPresence(ServerAvailabilityStatus availability) => availability switch
    {
        ServerAvailabilityStatus.Found => ConfigPresenceStatus.Found,
        ServerAvailabilityStatus.NotFound => ConfigPresenceStatus.NotFound,
        ServerAvailabilityStatus.PermissionDenied => ConfigPresenceStatus.PermissionDenied,
        ServerAvailabilityStatus.Offline => ConfigPresenceStatus.Offline,
        _ => ConfigPresenceStatus.Unknown,
    };
}
