using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Detection;

/// <summary>One plugin's claim on a sampled file: the format it would read the file as, and how sure it is.</summary>
public sealed record DetectionCandidate(
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("variant")] string? Variant,
    [property: JsonPropertyName("confidence")] double Confidence);
