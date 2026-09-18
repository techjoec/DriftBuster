using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>A named set of settings (or whole files) the user wants to look at together.</summary>
public sealed record CurationGroup
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("members")]
    public IReadOnlyList<CurationTarget> Members { get; init; } = [];
}
