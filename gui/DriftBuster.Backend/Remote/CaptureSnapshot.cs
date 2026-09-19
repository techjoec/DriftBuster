using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Remote;

/// <summary>A capture (<c>&lt;id&gt;-snapshot.json</c>): who took it and why, every detection and hunt hit, and the profile store's summary.</summary>
public sealed record CaptureSnapshot(CaptureInfo Capture, IReadOnlyList<CaptureDetection> Detections, DetectionProfileSummary? ProfileSummary, IReadOnlyList<HuntHitResult> HuntHits);
