namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Subset of the detection profile store used by profile-aware scans (the port of <c>ProfileMatcher</c>).</summary>
public interface IProfileMatcher
{
    IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IReadOnlySet<string> tags, string? relativePath);
}
