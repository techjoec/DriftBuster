namespace DriftBuster.Backend.Profiles.Detection;

public sealed record HuntBridgeMatch(string Profile, string Config, IReadOnlyList<string> ProfileTags, string? ExpectedFormat, string? ExpectedVariant);
