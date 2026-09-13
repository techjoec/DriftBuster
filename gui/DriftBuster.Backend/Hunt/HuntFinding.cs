namespace DriftBuster.Backend.Hunt;

/// <summary><c>driftbuster.hunt.HuntHit</c>: one line a rule matched.</summary>
/// <param name="Rule">The rule that matched.</param>
/// <param name="Path">The file, spelled as the walk produced it (the root joined with the relative path).</param>
/// <param name="LineNumber">One-based line number within the decoded sample.</param>
/// <param name="Excerpt">The stripped line.</param>
/// <param name="Matches">Captured values: groups up to <c>lastindex</c> then the whole match, stripped and deduplicated.</param>
public sealed record HuntFinding(HuntRule Rule, string Path, int LineNumber, string Excerpt, IReadOnlyList<string> Matches);
