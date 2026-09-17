using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// A diff result: the canonical payloads (clamped, never redacted), the unified diff
/// (redacted and clamped), its statistics and the plan values that produced it.
/// </summary>
public sealed record DiffArtifact
{
    public required string CanonicalBefore { get; init; }

    public required string CanonicalAfter { get; init; }

    public required string Diff { get; init; }

    public required DiffStats Stats { get; init; }

    public required string ContentType { get; init; }

    public required string FromLabel { get; init; }

    public required string ToLabel { get; init; }

    public string? Label { get; init; }

    /// <summary>The redactor's ordered tokens, the given mask tokens when no redactor was built, or null.</summary>
    public IReadOnlyList<string>? MaskTokens { get; init; }

    public string Placeholder { get; init; } = RedactionFilter.DefaultPlaceholder;

    public int ContextLines { get; init; } = 3;

    /// <summary>Per-token replacement counts in first-hit order when a redactor ran, otherwise null.</summary>
    public IReadOnlyDictionary<string, int>? RedactionCounts { get; init; }

    public IReadOnlyList<BinarySegmentEvidence>? BinaryEvidence { get; init; }

    public DiffSafetyLimits? SafetyLimits { get; init; }
}
