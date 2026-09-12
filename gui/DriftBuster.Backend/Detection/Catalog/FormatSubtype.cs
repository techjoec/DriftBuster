namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>Subtype definition for a detection class.</summary>
public sealed record FormatSubtype(
    string Name,
    int Priority,
    string? Variant = null,
    string? Severity = null,
    IReadOnlyList<string>? Aliases = null,
    string? SeverityHint = null,
    IReadOnlyList<RemediationHint>? RemediationHints = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
    public IReadOnlyList<RemediationHint> RemediationHints { get; init; } = RemediationHints ?? [];
}
