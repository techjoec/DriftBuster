using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary>A multi-server scan request (<c>driftbuster multi-server</c> stdin): the plans, and optionally the schema version and cache directory.</summary>
public sealed record MultiServerRequest
{
    public string? SchemaVersion { get; init; }

    public IReadOnlyList<ServerScanPlan> Plans { get; init => field = value ?? []; } = [];

    /// <summary>Where diff cache entries go; the data root's <c>cache/diffs</c> when null.</summary>
    public string? CacheDir { get; init; }
}
