namespace DriftBuster.Backend.Models;

/// <summary>A redaction or guard: the file (as displayed), the rule and the 1-based line; <see cref="Snippet"/> is empty for a guard.</summary>
public sealed record SecretFindingResult(string Path, string Rule, int Line, string Snippet);
