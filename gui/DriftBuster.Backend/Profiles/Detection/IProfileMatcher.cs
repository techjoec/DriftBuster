namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Subset of the detection profile store used by profile-aware scans.</summary>
public interface IProfileMatcher
{
    IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IReadOnlySet<string> tags, string? relativePath);
}
