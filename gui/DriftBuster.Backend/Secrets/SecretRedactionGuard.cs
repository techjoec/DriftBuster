namespace DriftBuster.Backend.Secrets;

/// <summary>
/// One rule stopped on one line because the line went past <see cref="SecretScanner.GuardBudget"/> replacements
/// inside inserted <c>[SECRET]</c> text, where redaction would otherwise never finish.
/// </summary>
public sealed record SecretRedactionGuard(string Path, string Rule, int Line);
