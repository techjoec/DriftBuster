using System.Text.Json.Nodes;

namespace DriftBuster.Cli.Commands;

/// <summary>One file of <c>scan --json</c>: its path under the root and, when it was detected, the match and its metadata.</summary>
internal sealed record ScanLine(
    string Path,
    bool Detected,
    string? Format = null,
    string? Variant = null,
    double? Confidence = null,
    string? Severity = null,
    string? SeverityHint = null,
    JsonObject? Metadata = null,
    IReadOnlyList<DriftBuster.Backend.Detection.DetectionCandidate>? Candidates = null);
