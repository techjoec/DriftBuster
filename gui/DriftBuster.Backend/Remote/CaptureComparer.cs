using System.Globalization;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Remote;

/// <summary>Two capture snapshots compared, and the comparison written as the summary <c>capture compare</c> prints.</summary>
public static class CaptureComparer
{
    public static CaptureComparison Compare(CaptureSnapshot baseline, CaptureSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var before = ByKey(baseline);
        var after = ByKey(current);
        var changed = after.Keys.Where(key => before.TryGetValue(key, out var old) && !string.Equals(Signature(old), Signature(after[key]), StringComparison.Ordinal));
        var (beforeTokens, beforeUnexpected) = Tokens(baseline.HuntHits);
        var (afterTokens, afterUnexpected) = Tokens(current.HuntHits);
        return new CaptureComparison(
            Sorted(after.Keys.Where(key => !before.ContainsKey(key))),
            Sorted(before.Keys.Where(key => !after.ContainsKey(key))),
            Sorted(changed),
            baseline.ProfileSummary is { } oldSummary && current.ProfileSummary is { } newSummary ? DetectionProfileCommands.Diff(oldSummary, newSummary) : null,
            [.. afterTokens.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var was = beforeTokens.GetValueOrDefault(pair.Key);
                return new CaptureTokenCount(pair.Key, was, pair.Value, pair.Value - was);
            })],
            new CaptureCountDelta(beforeUnexpected, afterUnexpected, afterUnexpected - beforeUnexpected));
    }

    public static void Write(CaptureComparison comparison, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(stdout);
        var lines = new List<string>
        {
            "Snapshot comparison summary",
            "===========================",
            Line($"Added detections: {comparison.AddedKeys.Count}"),
            Line($"Removed detections: {comparison.RemovedKeys.Count}"),
            Line($"Changed detections: {comparison.ChangedKeys.Count}"),
            string.Empty,
        };
        if (comparison.ProfileDiff is { } diff)
        {
            lines.Add("Profile summary diff:");
            lines.Add($"  Added profiles: {JoinOrNone(diff.AddedProfiles)}");
            lines.Add($"  Removed profiles: {JoinOrNone(diff.RemovedProfiles)}");
            lines.Add(Line($"  Changed profiles: {diff.ChangedProfiles.Count}"));
        }
        else
        {
            lines.Add("Profile summary diff unavailable (missing summaries).");
        }

        lines.Add(string.Empty);
        lines.Add("Dynamic token overview:");
        if (comparison.ExpectedTokens.Count == 0)
        {
            lines.Add("  Expected tokens: none");
        }
        else
        {
            lines.Add("  Expected tokens:");
            lines.AddRange(comparison.ExpectedTokens.Select(token => Line($"    {token.Token}: {token.Baseline} -> {token.Current} (delta {Signed(token.Delta)})")));
        }

        var unexpected = comparison.UnexpectedHits;
        lines.Add(Line($"  Unexpected hits: {unexpected.Baseline} -> {unexpected.Current} (delta {Signed(unexpected.Delta)})"));
        Keys("Added detection keys", comparison.AddedKeys);
        Keys("Removed detection keys", comparison.RemovedKeys);
        Keys("Changed detection keys", comparison.ChangedKeys);
        stdout.Write(string.Join('\n', lines) + "\n");

        void Keys(string heading, IReadOnlyList<CaptureDetectionKey> keys)
        {
            if (keys.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add(heading + ":");
                lines.AddRange(keys.Select(key => "  " + key));
            }
        }
    }

    private static CaptureDetectionKey[] Sorted(IEnumerable<CaptureDetectionKey> keys)
        => [.. keys.OrderBy(key => key.Location, StringComparer.Ordinal).ThenBy(key => key.Format, StringComparer.Ordinal).ThenBy(key => key.Variant, StringComparer.Ordinal)];

    // Detections by file, format and variant; a later detection with the same key replaces an earlier one.
    private static Dictionary<CaptureDetectionKey, CaptureDetection> ByKey(CaptureSnapshot snapshot)
    {
        var map = new Dictionary<CaptureDetectionKey, CaptureDetection>();
        foreach (var detection in snapshot.Detections)
        {
            map[new CaptureDetectionKey(string.IsNullOrEmpty(detection.RelativePath) ? detection.Path : detection.RelativePath, detection.Format, detection.Variant)] = detection;
        }

        return map;
    }

    // What a detection found, ignoring where the file lives and which profiles matched it.
    private static string Signature(CaptureDetection detection)
        => ModelJson.Serialize(detection with { Path = string.Empty, RelativePath = string.Empty, Profiles = [] });

    // Hits per rule token; hits whose rule has no token are unexpected.
    private static (Dictionary<string, int> Tokens, int Unexpected) Tokens(IEnumerable<Hunt.HuntHitResult> hits)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var unexpected = 0;
        foreach (var hit in hits)
        {
            if (string.IsNullOrEmpty(hit.Rule.TokenName))
            {
                unexpected++;
            }
            else
            {
                tokens[hit.Rule.TokenName] = tokens.GetValueOrDefault(hit.Rule.TokenName) + 1;
            }
        }

        return (tokens, unexpected);
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string JoinOrNone(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);

    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);
}
