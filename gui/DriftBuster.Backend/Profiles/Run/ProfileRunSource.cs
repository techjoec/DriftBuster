namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// What a run did with one source, as the offline runner's manifest summarises it: the source path, the directory its files went to,
/// whether it was optional, whether it was skipped and why (<c>missing</c> for a path that does not exist, <c>no-matches</c> for a glob
/// that matched nothing), the relative paths copied, and its exclude patterns.
/// </summary>
public sealed record ProfileRunSource(
    string Path,
    string Directory,
    bool Optional,
    bool Skipped,
    string? Reason,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Exclude);
