namespace DriftBuster.Backend.Remote;

/// <summary>What <see cref="CaptureRunner.CompareSnapshots"/> found.</summary>
/// <param name="ExitCode">0 for a comparison or a missing baseline, 1 when the current snapshot is missing or unreadable.</param>
/// <param name="Payload">
/// The comparison, or null when nothing was compared: sorted <c>added_keys</c>, <c>removed_keys</c>, <c>changed_keys</c> (each
/// <c>[path, format, variant]</c>), <c>profile_diff</c> (summary diff, or null without both summaries), <c>expected_tokens</c>
/// (<c>{"token","baseline","current","delta"}</c> per current token) and <c>unexpected_hits</c>.
/// </param>
public sealed record CaptureComparison(int ExitCode, OrderedDictionary<string, object?>? Payload);
