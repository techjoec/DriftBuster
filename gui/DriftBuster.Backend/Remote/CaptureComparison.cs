namespace DriftBuster.Backend.Remote;

/// <summary>What <see cref="CaptureRunner.CompareSnapshots"/> found.</summary>
/// <param name="ExitCode">0 for a comparison or a missing baseline, 1 when the current snapshot is missing or a snapshot cannot be read.</param>
/// <param name="Payload">
/// The comparison the text summary prints, or null when nothing was compared: <c>added_keys</c>, <c>removed_keys</c> and
/// <c>changed_keys</c> (each key a <c>[path, format, variant]</c> list, sorted), <c>profile_diff</c> (the
/// <c>diff_summary_snapshots</c> payload, or null without both summaries), <c>expected_tokens</c> (one
/// <c>{"token", "baseline", "current", "delta"}</c> entry per token the current snapshot holds, in token order) and
/// <c>unexpected_hits</c> (<c>{"baseline", "current", "delta"}</c>).
/// </param>
public sealed record CaptureComparison(int ExitCode, OrderedDictionary<string, object?>? Payload);
