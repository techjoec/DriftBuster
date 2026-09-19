namespace DriftBuster.Backend.Models;

/// <summary>
/// One source of a run profile: a file, directory or glob (<c>%VAR%</c> and a leading <c>~</c> expanded), an optional alias naming
/// the directory its files are copied under, whether it may be missing or match nothing, and exclude patterns matched against each
/// file's relative path and name.
/// </summary>
public sealed record RunProfileSource
{
    public required string Path { get; init; }

    public string? Alias { get; init; }

    public bool Optional { get; init; }

    public IReadOnlyList<string> Exclude { get; init => field = value ?? []; } = [];
}
