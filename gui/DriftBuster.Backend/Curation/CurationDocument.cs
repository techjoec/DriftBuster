namespace DriftBuster.Backend.Curation;

/// <summary>Everything the user has decided about settings: groups, rules, ignore and mask choices, and the review list.</summary>
public sealed record CurationDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<CurationGroup> Groups { get; init => field = value ?? []; } = [];

    public IReadOnlyList<CurationRule> Rules { get; init => field = value ?? []; } = [];

    public IReadOnlyList<CurationChoice> Choices { get; init => field = value ?? []; } = [];

    public IReadOnlyList<CurationReviewItem> Review { get; init => field = value ?? []; } = [];
}
