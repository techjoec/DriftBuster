namespace DriftBuster.Backend.Models;

/// <summary>Where to write an offline collector package, extra config metadata, and an optional config file name.</summary>
public sealed record OfflineCollectorRequest
{
    public required string PackagePath { get; init; }

    public IDictionary<string, string> Metadata { get; init => field = value ?? new Dictionary<string, string>(StringComparer.Ordinal); } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ConfigFileName { get; init; }
}
