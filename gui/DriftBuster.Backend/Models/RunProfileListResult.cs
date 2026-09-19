namespace DriftBuster.Backend.Models;

/// <summary>Every saved run profile, by directory name.</summary>
public sealed record RunProfileListResult(IReadOnlyList<RunProfileDefinition> Profiles);
