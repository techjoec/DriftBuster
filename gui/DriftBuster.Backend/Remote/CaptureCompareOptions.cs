namespace DriftBuster.Backend.Remote;

/// <summary>The <c>driftbuster capture compare</c> arguments: the baseline and current snapshot JSON paths.</summary>
/// <param name="Baseline">The baseline snapshot; a missing file means there is nothing to compare yet.</param>
/// <param name="Current">The current snapshot; it must exist.</param>
public sealed record CaptureCompareOptions(string Baseline, string Current);
