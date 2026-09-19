namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Two summaries compared (<c>detection-profile diff</c>): profiles added and removed, and profiles whose config ids changed.</summary>
public sealed record DetectionProfileSummaryDiff(
    DetectionProfileSummaryTotals Baseline,
    DetectionProfileSummaryTotals Current,
    IReadOnlyList<string> AddedProfiles,
    IReadOnlyList<string> RemovedProfiles,
    IReadOnlyList<DetectionProfileChange> ChangedProfiles);
