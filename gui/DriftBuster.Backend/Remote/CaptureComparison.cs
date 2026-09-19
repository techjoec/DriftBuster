using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Remote;

/// <summary>
/// Two captures compared: detections added, removed and changed (same file, format and variant, different detection), the profile
/// summary diff when both have one, and hunt hit counts per token.
/// </summary>
public sealed record CaptureComparison(
    IReadOnlyList<CaptureDetectionKey> AddedKeys,
    IReadOnlyList<CaptureDetectionKey> RemovedKeys,
    IReadOnlyList<CaptureDetectionKey> ChangedKeys,
    DetectionProfileSummaryDiff? ProfileDiff,
    IReadOnlyList<CaptureTokenCount> ExpectedTokens,
    CaptureCountDelta UnexpectedHits);
