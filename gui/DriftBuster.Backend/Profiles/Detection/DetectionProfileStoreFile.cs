namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>A detection profile store file: <c>{"profiles": [...]}</c>.</summary>
public sealed record DetectionProfileStoreFile(IReadOnlyList<DetectionProfile> Profiles);
