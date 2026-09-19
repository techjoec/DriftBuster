namespace DriftBuster.Backend.Curation;

/// <summary>A setting picked for review ("I want this one"); it stays until the user removes it.</summary>
public sealed record CurationReviewItem
{
    public CurationTarget Target { get; init => field = value ?? new(); } = new();

    public string Note { get; init => field = value ?? string.Empty; } = string.Empty;

    public DateTimeOffset AddedAt { get; init; }
}
