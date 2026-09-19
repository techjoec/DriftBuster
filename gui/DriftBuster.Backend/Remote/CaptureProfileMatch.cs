using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Remote;

public sealed record CaptureProfileMatch(string Profile, IReadOnlyList<string> ProfileTags, DetectionProfileConfig Config);
