namespace DriftBuster.Backend.Models;

/// <summary>
/// The secret filter's part of a run: the ruleset version and whether rules loaded, the ignore lists in force, every redaction, each
/// rule stopped on a line where redaction would never finish, and the log lines.
/// </summary>
public sealed record SecretRunSummary(
    string RulesetVersion,
    bool RulesLoaded,
    IReadOnlyList<string> IgnoredRules,
    IReadOnlyList<string> IgnoredPatterns,
    IReadOnlyList<SecretFindingResult> Findings,
    IReadOnlyList<SecretFindingResult> RedactionGuards,
    IReadOnlyList<string> Messages);
