namespace DriftBuster.Backend.Hunt;

/// <summary>A suggested token substitution derived from a hunt hit.</summary>
public sealed record PlanTransform(string TokenName, string Value, string Placeholder, string RuleName, string Path, int LineNumber, string Excerpt);
