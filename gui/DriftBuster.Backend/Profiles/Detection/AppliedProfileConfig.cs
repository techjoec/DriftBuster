namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Specific profile/config pairing that applies to a path.</summary>
public sealed record AppliedProfileConfig(DetectionProfile Profile, DetectionProfileConfig Config);
