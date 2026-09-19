namespace DriftBuster.Backend.Models;

/// <summary>Secret scanner rule names to ignore, and regular expressions whose matches are never reported.</summary>
public sealed record SecretScannerOptions
{
    public IReadOnlyList<string> IgnoreRules { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> IgnorePatterns { get; init => field = value ?? []; } = [];
}
