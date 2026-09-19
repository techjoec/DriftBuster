namespace DriftBuster.Backend.Models;

/// <summary>
/// What a run did with one source: the directory its files went to, whether it was skipped and why (<c>missing</c> for a path that
/// does not exist, <c>no-matches</c> for a glob that matched nothing), and the relative paths copied.
/// </summary>
public sealed record RunProfileSourceResult(string Path, string Directory, bool Optional, bool Skipped, string? Reason, IReadOnlyList<string> Matched);
