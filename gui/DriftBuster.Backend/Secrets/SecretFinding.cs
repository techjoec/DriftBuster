namespace DriftBuster.Backend.Secrets;

/// <summary>One redaction, with the redacted line (first 200 code points).</summary>
public sealed record SecretFinding(string Path, string Rule, int Line, string Snippet);
