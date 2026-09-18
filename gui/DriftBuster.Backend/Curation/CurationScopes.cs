using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// Where a choice applies: every run (an empty scope) or one host set, identified by its hosts' labels and roots so the same
/// servers scanned again get the same id.
/// </summary>
public static class CurationScopes
{
    public const string AllRuns = "";

    public static string HostSetId(IEnumerable<MultiServerPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var lines = plans
            .Select(plan => $"{plan.Label.Trim()}|{string.Join('|', plan.Roots.Select(root => root.Trim()).Order(StringComparer.OrdinalIgnoreCase))}")
            .Order(StringComparer.OrdinalIgnoreCase);
        return "hosts-" + CurationTarget.ValueHashOf(string.Join('\n', lines).ToUpperInvariant())[..12];
    }

    public static bool Applies(string scope, string hostSetId) =>
        scope.Length == 0 || string.Equals(scope, hostSetId, StringComparison.Ordinal);
}
