using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>Everything the user has decided about settings: groups, rules, ignore and mask choices, and the review list.</summary>
public sealed record CurationDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("groups")]
    public IReadOnlyList<CurationGroup> Groups { get; init; } = [];

    [JsonPropertyName("rules")]
    public IReadOnlyList<CurationRule> Rules { get; init; } = [];

    [JsonPropertyName("choices")]
    public IReadOnlyList<CurationChoice> Choices { get; init; } = [];

    [JsonPropertyName("review")]
    public IReadOnlyList<CurationReviewItem> Review { get; init; } = [];
}
