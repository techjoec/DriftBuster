using System.Globalization;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Remote;

/// <summary><c>driftbuster capture compare</c>.</summary>
public static partial class CaptureRunner
{
    /// <summary>
    /// <c>compare_snapshots(args)</c>: refuses a missing current snapshot (exit code 1), reports a missing baseline as the first capture
    /// (exit code 0), loads both snapshots (a load failure written to <paramref name="stderr"/>, exit code 1), then writes the comparison
    /// summary to <paramref name="stdout"/>: added, removed and changed detection counts, the profile summary diff, the expected hunt tokens
    /// of the current snapshot against the baseline, the unexpected hits, and the added, removed and changed detection keys.
    /// </summary>
    /// <remarks>
    /// Detections are keyed by <c>(relative_path or path, format, variant)</c> tuples in dicts and sets with Python's equality and hashing
    /// (<see cref="EngineValues"/>), so <c>1</c>, <c>1.0</c> and <c>true</c> are one key and a list in a key raises <c>TypeError</c>; keys
    /// sort with Python's tuple ordering. A snapshot that is not a mapping raises <c>AttributeError</c>, as it does in Python, and an error
    /// met while the summary is written (a profile name that is not a str) escapes after the lines already written.
    /// </remarks>
    public static CaptureComparison CompareSnapshots(CaptureCompareOptions options, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var baselinePath = LexicalPath.Str(options.Baseline);
        var currentPath = LexicalPath.Str(options.Current);
        if (!RunProfileStore.Exists(currentPath))
        {
            stderr.Write($"error: current snapshot not found: {currentPath}\n");
            return new CaptureComparison(1, null);
        }

        if (!RunProfileStore.Exists(baselinePath))
        {
            stdout.Write("No baseline snapshot found; record this run as the first capture.\n");
            return new CaptureComparison(0, null);
        }

        object? baseline;
        object? current;
        try
        {
            baseline = LoadSnapshot(baselinePath);
            current = LoadSnapshot(currentPath);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            stderr.Write($"error: {exc.Message}\n");
            return new CaptureComparison(1, null);
        }

        var state = BuildComparison(baseline, current);
        var expectedTokens = WriteComparison(state, stdout);
        return new CaptureComparison(0, ComparisonPayload(state, expectedTokens));
    }

    /// <summary>
    /// <c>_load_snapshot(path)</c>: <c>json.loads(path.read_text())</c>. Text that is not a JSON document raises
    /// <c>ValueError("Failed to parse snapshot {path}: invalid JSON document")</c> (<see cref="EngineJson"/> reports no decoder reason);
    /// read, decode and limit errors are raised as they are.
    /// </summary>
    public static object? LoadSnapshot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return EngineJson.TryLoadsOrRaiseLimits(ReadUtf8Text(path), out var value)
            ? value
            : throw new EngineValueException($"Failed to parse snapshot {path}: invalid JSON document", nameof(path));
    }

    /// <summary>
    /// <c>_detection_key(entry)</c>: <c>(relative_path or path, detection.format, detection.variant)</c> as a three-item tuple
    /// (<see cref="object"/> array), a missing <c>detection</c> reading as an empty mapping.
    /// </summary>
    public static object?[] DetectionKey(object? entry)
    {
        var detection = DetectionProfileStore.GetOrDefault(entry, "detection", new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        var relative = EngineBuiltins.Get(entry, "relative_path");
        var location = EngineBuiltins.IsTruthy(relative) ? relative : EngineBuiltins.Get(entry, "path");
        return [location, EngineBuiltins.Get(detection, "format"), EngineBuiltins.Get(detection, "variant")];
    }

    /// <summary><c>_detection_signature(entry)</c>: <c>json.dumps(detection, sort_keys=True)</c> of the entry's detection.</summary>
    public static string DetectionSignature(object? entry)
    {
        var detection = DetectionProfileStore.GetOrDefault(entry, "detection", new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        return Canonicaliser.Dumps(detection, indent: false, ensureAscii: true, sortKeys: true);
    }

    /// <summary>
    /// <c>_hunt_token_summary(hits)</c>: the count of hits per truthy <c>rule.token_name</c>, keyed with Python's dict semantics in first-seen
    /// order, and the count of hits without one.
    /// </summary>
    public static (IReadOnlyList<KeyValuePair<object, long>> Expected, long Unexpected) HuntTokenSummary(object? hits)
    {
        var counts = new Dictionary<object, long>(EngineValues.HashKeys!);
        var order = new List<object>();
        long unexpected = 0;
        foreach (var hit in EngineBuiltins.Iterate(hits))
        {
            var rule = DetectionProfileStore.GetOrDefault(hit, "rule", new OrderedDictionary<string, object?>(StringComparer.Ordinal));
            var token = EngineBuiltins.Get(rule, "token_name");
            if (!EngineBuiltins.IsTruthy(token))
            {
                unexpected++;
                continue;
            }

            if (counts.TryGetValue(token!, out var count))
            {
                counts[token!] = count + 1;
            }
            else
            {
                counts[token!] = 1;
                order.Add(token!);
            }
        }

        return (order.Select(token => new KeyValuePair<object, long>(token, counts[token])).ToList(), unexpected);
    }

    private sealed record ComparisonState(
        IReadOnlyList<object?> Added,
        IReadOnlyList<object?> Removed,
        IReadOnlyList<object?> Changed,
        OrderedDictionary<string, object?>? ProfileDiff,
        IReadOnlyList<KeyValuePair<object, long>> BaselineExpected,
        long BaselineUnexpected,
        IReadOnlyList<KeyValuePair<object, long>> CurrentExpected,
        long CurrentUnexpected);

    // Everything compare_snapshots computes before it writes the first line, in its order.
    private static ComparisonState BuildComparison(object? baseline, object? current)
    {
        var baselineMap = DetectionMap(DetectionProfileStore.GetOrDefault(baseline, "detections", new List<object?>()));
        var currentMap = DetectionMap(DetectionProfileStore.GetOrDefault(current, "detections", new List<object?>()));

        var added = EngineValues.Sorted(currentMap.Keys.Where(key => !baselineMap.Entries.ContainsKey(key)));
        var removed = EngineValues.Sorted(baselineMap.Keys.Where(key => !currentMap.Entries.ContainsKey(key)));
        // set(baseline) & set(current) iterates the smaller set (the current one on a tie) and keeps its key objects.
        var (smaller, larger) = currentMap.Keys.Count <= baselineMap.Keys.Count ? (currentMap, baselineMap) : (baselineMap, currentMap);
        var changed = EngineValues.Sorted(smaller.Keys
            .Where(key => larger.Entries.ContainsKey(key))
            .Where(key => !string.Equals(DetectionSignature(baselineMap.Entries[key]), DetectionSignature(currentMap.Entries[key]), StringComparison.Ordinal)));

        var baselineSummary = EngineBuiltins.Get(baseline, "profile_summary");
        var currentSummary = EngineBuiltins.Get(current, "profile_summary");
        OrderedDictionary<string, object?>? profileDiff = null;
        if (EngineBuiltins.IsTruthy(baselineSummary) && EngineBuiltins.IsTruthy(currentSummary))
        {
            profileDiff = DetectionProfileStore.DiffSummarySnapshots(baselineSummary, currentSummary);
        }

        var (baselineExpected, baselineUnexpected) = HuntTokenSummary(DetectionProfileStore.GetOrDefault(baseline, "hunt_hits", new List<object?>()));
        var (currentExpected, currentUnexpected) = HuntTokenSummary(DetectionProfileStore.GetOrDefault(current, "hunt_hits", new List<object?>()));
        return new ComparisonState(added, removed, changed, profileDiff, baselineExpected, baselineUnexpected, currentExpected, currentUnexpected);
    }

    // The summary lines, written as they are produced; returns the expected token entries in the order they were written.
    private static List<object?> WriteComparison(ComparisonState state, TextWriter stdout)
    {
        stdout.Write("Snapshot comparison summary\n");
        stdout.Write("===========================\n");
        stdout.Write(string.Create(CultureInfo.InvariantCulture, $"Added detections: {state.Added.Count}\nRemoved detections: {state.Removed.Count}\n"));
        stdout.Write(string.Create(CultureInfo.InvariantCulture, $"Changed detections: {state.Changed.Count}\n"));

        if (state.ProfileDiff is { } diff)
        {
            stdout.Write("\nProfile summary diff:\n");
            stdout.Write($"  Added profiles: {JoinOrNone((IEnumerable<object?>)diff["added_profiles"]!)}\n");
            stdout.Write($"  Removed profiles: {JoinOrNone((IEnumerable<object?>)diff["removed_profiles"]!)}\n");
            stdout.Write(string.Create(CultureInfo.InvariantCulture, $"  Changed profiles: {((IReadOnlyCollection<object?>)diff["changed_profiles"]!).Count}\n"));
        }
        else
        {
            stdout.Write("\nProfile summary diff unavailable (missing summaries).\n");
        }

        stdout.Write("\nDynamic token overview:\n");
        var tokens = new List<object?>();
        if (state.CurrentExpected.Count > 0)
        {
            stdout.Write("  Expected tokens:\n");
            tokens = WriteExpectedTokens(state, stdout);
        }
        else
        {
            stdout.Write("  Expected tokens: none\n");
        }

        stdout.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"  Unexpected hits: {state.BaselineUnexpected} -> {state.CurrentUnexpected} (delta {Signed(state.CurrentUnexpected - state.BaselineUnexpected)})\n"));
        WriteKeys(stdout, "Added detection keys", state.Added);
        WriteKeys(stdout, "Removed detection keys", state.Removed);
        WriteKeys(stdout, "Changed detection keys", state.Changed);
        return tokens;
    }

    // for token, count in sorted(current_expected.items()): each written with its baseline count and the signed delta.
    private static List<object?> WriteExpectedTokens(ComparisonState state, TextWriter stdout)
    {
        var baselineCounts = new Dictionary<object, long>(EngineValues.HashKeys!);
        foreach (var (token, count) in state.BaselineExpected)
        {
            baselineCounts[token] = count;
        }

        var entries = new List<object?>();
        foreach (var item in EngineValues.Sorted(state.CurrentExpected.Select(object? (pair) => new object?[] { pair.Key, pair.Value })))
        {
            var pair = (object?[])item!;
            var token = pair[0]!;
            var count = (long)pair[1]!;
            var baselineCount = baselineCounts.GetValueOrDefault(token);
            stdout.Write(string.Create(CultureInfo.InvariantCulture, $"    {EngineRepr.Str(token)}: {baselineCount} -> {count} (delta {Signed(count - baselineCount)})\n"));
            var entry = Delta(baselineCount, count);
            entry.Insert(0, "token", token);
            entries.Add(entry);
        }

        return entries;
    }

    private static void WriteKeys(TextWriter stdout, string heading, IReadOnlyList<object?> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        stdout.Write($"\n{heading}:\n");
        foreach (var key in keys)
        {
            stdout.Write($"  {EngineRepr.Repr(key)}\n");
        }
    }

    // ', '.join(items) or 'none': every item must be a str (TypeError "sequence item N: expected str instance, T found").
    private static string JoinOrNone(IEnumerable<object?> items)
    {
        var builder = new StringBuilder();
        var index = 0;
        foreach (var item in items)
        {
            if (item is not string text)
            {
                throw new EngineTypeException(
                    string.Create(CultureInfo.InvariantCulture, $"sequence item {index}: expected str instance, {EngineBuiltins.TypeName(item)} found"),
                    nameof(items));
            }

            builder.Append(index++ == 0 ? string.Empty : ", ").Append(text);
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    // format(value, "+d").
    private static string Signed(long value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);

    private static OrderedDictionary<string, object?> ComparisonPayload(ComparisonState state, List<object?> expectedTokens) => new(StringComparer.Ordinal)
    {
        ["added_keys"] = state.Added.Select(KeyList).ToList(),
        ["removed_keys"] = state.Removed.Select(KeyList).ToList(),
        ["changed_keys"] = state.Changed.Select(KeyList).ToList(),
        ["profile_diff"] = state.ProfileDiff,
        ["expected_tokens"] = expectedTokens,
        ["unexpected_hits"] = Delta(state.BaselineUnexpected, state.CurrentUnexpected),
    };

    private static object? KeyList(object? key) => ((object?[])key!).ToList();

    private static OrderedDictionary<string, object?> Delta(long baseline, long current) => new(StringComparer.Ordinal)
    {
        ["baseline"] = baseline,
        ["current"] = current,
        ["delta"] = current - baseline,
    };

    private sealed class DetectionEntries
    {
        public Dictionary<object, object?> Entries { get; } = new(EngineValues.HashKeys!);

        public List<object> Keys { get; } = [];
    }

    // { _detection_key(entry): entry for entry in detections }: the first key object of equal keys is kept, the last entry wins.
    private static DetectionEntries DetectionMap(object? detections)
    {
        var map = new DetectionEntries();
        foreach (var entry in EngineBuiltins.Iterate(detections))
        {
            var key = DetectionKey(entry);
            if (!map.Entries.ContainsKey(key))
            {
                map.Keys.Add(key);
            }

            map.Entries[key] = entry;
        }

        return map;
    }
}
