namespace DriftBuster.Backend.Secrets;

/// <summary><c>secret_rules.json</c>: the ruleset version and its rules.</summary>
public sealed record SecretRuleFile(string Version, IReadOnlyList<SecretRuleDefinition> Rules);
