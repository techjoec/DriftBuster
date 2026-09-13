namespace DriftBuster.Backend.Secrets;

/// <summary>The <c>(rules, version)</c> pair <c>compile_ruleset_from_mapping</c> returns.</summary>
public sealed record SecretRuleset(IReadOnlyList<SecretDetectionRule> Rules, string Version);
