using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Detection result annotated with matching configuration profiles.</summary>
public sealed record ProfiledDetection(string Path, DetectionMatch? Detection, IReadOnlyList<AppliedProfileConfig> Profiles);
