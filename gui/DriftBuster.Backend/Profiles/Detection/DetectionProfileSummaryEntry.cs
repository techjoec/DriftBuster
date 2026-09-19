namespace DriftBuster.Backend.Profiles.Detection;

public sealed record DetectionProfileSummaryEntry(string Name, string? Description, IReadOnlyList<string> Tags, IReadOnlyList<string> ConfigIds);
