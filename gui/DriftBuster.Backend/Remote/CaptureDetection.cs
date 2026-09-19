using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Remote;

/// <summary>A detected file: where it is, what it detected as and why, its metadata, and the profile configs it matched.</summary>
public sealed record CaptureDetection(
    string Path,
    string RelativePath,
    string Plugin,
    string Format,
    string? Variant,
    double Confidence,
    IReadOnlyList<string> Reasons,
    JsonNode? Metadata,
    IReadOnlyList<CaptureProfileMatch> Profiles);
