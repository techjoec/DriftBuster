namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>A configuration expectation inside a detection profile (the port of <c>ProfileConfig</c>).</summary>
public sealed record DetectionProfileConfig(string Identifier, IReadOnlyDictionary<string, object?>? Metadata = null);
