using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary>The <c>multi-server.v2</c> contract: the version and shape guarantees the GUI facade checks on every response.</summary>
public static class MultiServerSchema
{
    /// <summary><c>SCHEMA_VERSION</c>.</summary>
    public const string Version = "multi-server.v2";

    /// <summary>
    /// Rejects a response whose version is not <see cref="Version"/> (case-insensitive) and fills every null collection and a
    /// missing summary.
    /// </summary>
    public static void ValidateResponse(ServerScanResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(response.Version, Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported multi-server schema version '{response.Version}'. Expected '{Version}'.");
        }

        response.Results ??= [];
        response.Catalog ??= [];
        response.Drilldown ??= [];
        response.Comparison ??= new SettingsComparison();
        response.Comparison.Hosts ??= [];
        response.Comparison.Files ??= [];
        response.Summary ??= new ServerScanSummary
        {
            BaselineHostId = string.Empty,
            TotalHosts = response.Results.Length,
            ConfigsEvaluated = response.Catalog.Length,
            DriftingConfigs = response.Catalog.Count(entry => entry.DriftCount > 0),
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        foreach (var result in response.Results)
        {
            result.Roots ??= [];
        }

        foreach (var entry in response.Catalog)
        {
            entry.PresentHosts ??= [];
            entry.MissingHosts ??= [];
        }

        foreach (var drilldown in response.Drilldown)
        {
            drilldown.Servers ??= [];
            drilldown.Notes ??= [];
            drilldown.HostDiffs ??= [];
        }
    }
}
