using System.Text.Json;

using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>The <c>detection-profile</c> operations: <c>summary</c>, <c>diff</c> and <c>hunt-bridge</c>.</summary>
public static class DetectionProfileCommands
{
    public static DetectionProfileSummary Summary(string storePath) => DetectionProfileStore.Load(storePath).Summary();

    /// <summary>Two summaries compared; names and ids in ordinal order.</summary>
    public static DetectionProfileSummaryDiff Diff(string baselinePath, string currentPath)
        => Diff(DetectionProfileStore.Read(baselinePath, ModelJson.TypeInfo<DetectionProfileSummary>()), DetectionProfileStore.Read(currentPath, ModelJson.TypeInfo<DetectionProfileSummary>()));

    public static DetectionProfileSummaryDiff Diff(DetectionProfileSummary baselineSummary, DetectionProfileSummary currentSummary)
    {
        ArgumentNullException.ThrowIfNull(baselineSummary);
        ArgumentNullException.ThrowIfNull(currentSummary);
        var baseline = baselineSummary.Profiles.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var current = currentSummary.Profiles.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var changed = new List<DetectionProfileChange>();
        foreach (var name in current.Keys.Where(baseline.ContainsKey).Order(StringComparer.Ordinal))
        {
            var before = baseline[name].ConfigIds;
            var after = current[name].ConfigIds;
            var added = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var removed = before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (added.Length > 0 || removed.Length > 0 || before.Count != after.Count)
            {
                changed.Add(new DetectionProfileChange(name, before.Count, after.Count, added, removed));
            }
        }

        return new DetectionProfileSummaryDiff(
            Totals(baseline.Values),
            Totals(current.Values),
            [.. current.Keys.Where(name => !baseline.ContainsKey(name)).Order(StringComparer.Ordinal)],
            [.. baseline.Keys.Where(name => !current.ContainsKey(name)).Order(StringComparer.Ordinal)],
            changed);

        static DetectionProfileSummaryTotals Totals(IEnumerable<DetectionProfileSummaryEntry> entries)
            => new(entries.Count(), entries.Sum(entry => entry.ConfigIds.Count));
    }

    /// <summary>
    /// Each hit of a <c>driftbuster hunt</c> output file (a JSON array) with the configs its path matches: its <c>relative_path</c>, else
    /// its <c>path</c> relative to <paramref name="root"/> (its name when outside the root or without one).
    /// </summary>
    public static HuntBridgeResult HuntBridge(string storePath, string huntPath, IEnumerable<string>? tags, string? root)
    {
        var store = DetectionProfileStore.Load(storePath);
        var scanTags = (tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.Ordinal);
        using var hunts = ReadHunts(huntPath);
        var items = new List<HuntBridgeItem>();
        foreach (var hit in hunts.RootElement.EnumerateArray())
        {
            var relative = RelativePath(hit, root);
            var matches = store.MatchingConfigs(scanTags, relative)
                .Select(applied => new HuntBridgeMatch(
                    applied.Profile.Name,
                    applied.Config.Id,
                    [.. applied.Profile.Tags.Order(StringComparer.Ordinal)],
                    applied.Config.ExpectedFormat,
                    applied.Config.ExpectedVariant))
                .ToArray();
            items.Add(new HuntBridgeItem(hit.Clone(), relative, matches));
        }

        return new HuntBridgeResult(items);
    }

    internal static string? RelativePath(JsonElement hit, string? root)
    {
        if (hit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (hit.TryGetProperty("relative_path", out var relative) && relative.ValueKind == JsonValueKind.String && relative.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        if (!hit.TryGetProperty("path", out var pathValue) || pathValue.GetString() is not { Length: > 0 } path)
        {
            return null;
        }

        if (root is null)
        {
            return Path.GetFileName(path);
        }

        var fromRoot = Path.GetRelativePath(root, path);
        return fromRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(fromRoot) ? Path.GetFileName(path) : fromRoot.Replace('\\', '/');
    }

    private static JsonDocument ReadHunts(string path)
    {
        try
        {
            var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw new DetectionProfileException($"{path}: $: a hunt file holds a JSON array of hits.");
            }

            return document;
        }
        catch (JsonException exc)
        {
            throw new DetectionProfileException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new DetectionProfileException($"{path}: {exc.Message}", exc);
        }
    }
}
