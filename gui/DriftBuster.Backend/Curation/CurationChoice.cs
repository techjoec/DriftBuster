using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>One ignore, mask or unmask choice, for every run or for one host set (<see cref="Scope"/>).</summary>
public sealed record CurationChoice
{
    [JsonPropertyName("target")]
    public CurationTarget Target { get; init; } = new();

    /// <summary>One of <see cref="CurationChoiceKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = CurationChoiceKinds.Ignore;

    /// <summary>Empty for every run, otherwise the host set id (<see cref="CurationScopes.HostSetId"/>) it applies to.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}
