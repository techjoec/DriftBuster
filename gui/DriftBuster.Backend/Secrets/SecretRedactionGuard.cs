namespace DriftBuster.Backend.Secrets;

/// <summary>
/// Fix g: one rule the port stopped on one line because the line went past <see cref="SecretScanner.GuardBudget"/> replacements
/// inside inserted <c>[SECRET]</c> text, where Python's <c>copy_with_secret_filter</c> would never return.
/// </summary>
public sealed record SecretRedactionGuard(string Path, string Rule, int Line);
