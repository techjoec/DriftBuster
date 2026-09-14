namespace DriftBuster.Backend.MultiServer;

/// <summary><c>driftbuster.multi_server.ConfigRecord</c>: one detected configuration file on one host.</summary>
public sealed record ConfigRecord
{
    public required string ConfigId { get; init; }

    public required string DisplayName { get; init; }

    public required string FormatId { get; init; }

    /// <summary><c>xml</c> or <c>text</c> (<see cref="Diff.ContentTypeResolver.FromCatalogFormat"/>).</summary>
    public required string ContentType { get; init; }

    public required string Canonical { get; init; }

    /// <summary>The whole file decoded as UTF-8 with replacement and universal newlines.</summary>
    public required string Raw { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public required string FileHash { get; init; }

    public bool Secrets { get; init; }

    public bool Masked { get; init; }

    public required string SourcePath { get; init; }

    public required string PluginName { get; init; }

    public required string RelativePath { get; init; }
}
