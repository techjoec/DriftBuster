using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Detection;

/// <summary>A detection as it is written out: the file, the match's fields and a copy of its metadata.</summary>
public sealed record DetectionPayload(
    string Path,
    string Plugin,
    string Format,
    string? Variant,
    double Confidence,
    IReadOnlyList<string> Reasons,
    JsonObject Metadata);
