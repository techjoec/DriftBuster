namespace DriftBuster.Backend.Hunt;

/// <summary>The outcome of <see cref="HuntEngine.HuntPath"/>.</summary>
/// <param name="Hits">Hits in walk order, then rule order, then line order.</param>
/// <param name="RootDirectory">The directory relative paths are taken from: the root, or a file root's parent.</param>
/// <param name="UnreadableFiles">
/// Files skipped because opening or reading them failed (fix b: Python aborts the whole hunt on the first one).
/// </param>
public sealed record HuntScanResult(IReadOnlyList<HuntFinding> Hits, string RootDirectory, IReadOnlyList<string> UnreadableFiles);
