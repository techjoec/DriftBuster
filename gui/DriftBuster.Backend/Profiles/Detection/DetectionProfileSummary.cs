namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>A store's profiles by name with their tags and config ids (<c>detection-profile summary</c>).</summary>
public sealed record DetectionProfileSummary(int TotalProfiles, int TotalConfigs, IReadOnlyList<DetectionProfileSummaryEntry> Profiles);
