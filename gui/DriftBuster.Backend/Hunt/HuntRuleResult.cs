namespace DriftBuster.Backend.Hunt;

/// <summary>A hunt rule as written with its hits.</summary>
public sealed record HuntRuleResult(string Name, string Description, string? TokenName, IReadOnlyList<string> Keywords, IReadOnlyList<string> Patterns);
