using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>A setting picked for review ("I want this one"); it stays until the user removes it.</summary>
public sealed record CurationReviewItem
{
    [JsonPropertyName("target")]
    public CurationTarget Target { get; init; } = new();

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    [JsonPropertyName("added_at")]
    public DateTimeOffset AddedAt { get; init; }
}
