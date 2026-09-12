namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>Primary detection class definition.</summary>
public sealed record FormatClass(
    string Name,
    string Slug,
    int Priority,
    string DefaultSeverity,
    string? DefaultVariant = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<FormatSubtype>? Subtypes = null,
    string? SeverityHint = null,
    IReadOnlyList<RemediationHint>? RemediationHints = null,
    IReadOnlyList<string>? References = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
    public IReadOnlyList<FormatSubtype> Subtypes { get; init; } = Subtypes ?? [];
    public IReadOnlyList<RemediationHint> RemediationHints { get; init; } = RemediationHints ?? [];
    public IReadOnlyList<string> References { get; init; } = References ?? [];
}
