namespace DriftBuster.Backend.MultiServer;

/// <summary>One detected configuration file on one host.</summary>
public sealed record ConfigRecord
{
    public required string ConfigId { get; init; }

    public required string DisplayName { get; init; }

    public required string FormatId { get; init; }

    /// <summary><c>xml</c>, <c>json</c> or <c>text</c> (<see cref="Diff.ContentTypeResolver.FromCatalogFormat"/>).</summary>
    public required string ContentType { get; init; }

    public required string Canonical { get; init; }

    /// <summary>The whole file decoded as UTF-8 with replacement and universal newlines.</summary>
    public required string Raw { get; init; }

    public required string FileHash { get; init; }

    public bool Secrets { get; init; }

    public bool Masked { get; init; }

    public required string SourcePath { get; init; }

    public required string PluginName { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>The winning detection's confidence; 0 for records not produced by detection (registry reads).</summary>
    public double Confidence { get; init; }

    /// <summary>Every format the detector's plugins claimed for the file, strongest first.</summary>
    public IReadOnlyList<Detection.DetectionCandidate> Candidates { get; init; } = [];
}
