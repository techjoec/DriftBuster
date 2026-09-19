namespace DriftBuster.Backend.Profiles.Detection;

public sealed record DetectionProfileChange(string Name, int BaselineConfigCount, int CurrentConfigCount, IReadOnlyList<string> AddedConfigIds, IReadOnlyList<string> RemovedConfigIds);
