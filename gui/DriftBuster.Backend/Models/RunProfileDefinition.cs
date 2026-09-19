namespace DriftBuster.Backend.Models;

/// <summary>
/// A run profile (<c>Profiles/&lt;safe name&gt;/profile.json</c>): its sources in collection order, the baseline source path (the
/// first source when none), free-form options and the secret scanner's ignore lists.
/// </summary>
public sealed record RunProfileDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    // Source-generated reads pass null for an absent init-only member, so the setters restore the empty defaults.
    public IReadOnlyList<RunProfileSource> Sources { get; init => field = value ?? []; } = [];

    public string? Baseline { get; init; }

    public IReadOnlyDictionary<string, string> Options { get; init => field = value ?? EmptyOptions; } = EmptyOptions;

    public SecretScannerOptions SecretScanner { get; init => field = value ?? new(); } = new();

    // UI Automation reads a list item through ToString.
    public override string ToString() => Name;

    private static readonly IReadOnlyDictionary<string, string> EmptyOptions = new Dictionary<string, string>(StringComparer.Ordinal);
}
