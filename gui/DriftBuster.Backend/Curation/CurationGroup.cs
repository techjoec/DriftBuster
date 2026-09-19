namespace DriftBuster.Backend.Curation;

/// <summary>A named set of settings (or whole files) the user wants to look at together.</summary>
public sealed record CurationGroup
{
    public string Name { get; init => field = value ?? string.Empty; } = string.Empty;

    public string Description { get; init => field = value ?? string.Empty; } = string.Empty;

    public string Scope { get; init => field = value ?? string.Empty; } = string.Empty;

    public IReadOnlyList<CurationTarget> Members { get; init => field = value ?? []; } = [];
}
