using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// Detection-profile commands for the console tool: JSON loading with friendly errors, the store builder with a lenient fallback,
/// and <c>summary</c>, <c>diff</c> and <c>hunt-bridge</c>, each returning the payload to write. Parsing and exit codes stay in the CLI.
/// </summary>
public static class DetectionProfileCommands
{
    /// <summary>The strict store builder <see cref="StoreFromPayload"/> tries first (test seam; null skips it).</summary>
    internal static Func<object?, DetectionProfileStore>? FromDict { get; set; } = DetectionProfileStore.FromDict;

    /// <summary>
    /// The UTF-8 JSON value at <paramref name="path"/>. Read failures throw <see cref="IOException"/>
    /// (<c>Unable to read JSON payload from {path}: {reason}</c>); invalid JSON, non-UTF-8 bytes and decoder limits throw
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    public static object? LoadJson(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var shown = LexicalPath.Str(path);
        byte[] raw;
        try
        {
            raw = EngineTextFile.ReadBytes(shown, shown);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to read JSON payload from {shown}: {exc.Message}", exc);
        }

        return EngineJson.TryLoadsOrRaiseLimits(EngineUtf8.DecodeFile(raw), out var value)
            ? value
            : throw new InvalidDataException($"Failed to parse JSON from {shown}: invalid JSON document");
    }

    /// <summary>
    /// <see cref="DetectionProfileStore.FromDict"/>, or when that throws, a lenient build that skips non-object entries and configs
    /// and uses each <c>id</c> and <c>name</c> as text.
    /// </summary>
    public static DetectionProfileStore StoreFromPayload(object? payload)
    {
        if (FromDict is { } builder)
        {
            try
            {
                return builder(payload);
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                // Fall back to the manual build for any exception.
            }
        }

        var profiles = new List<DetectionProfile>();
        foreach (var entry in EngineBuiltins.Iterate(DetectionProfileStore.GetOrDefault(payload, "profiles", new List<object?>())))
        {
            if (entry is not IReadOnlyDictionary<string, object?>)
            {
                continue;
            }

            var configs = new List<DetectionProfileConfig>();
            foreach (var cfg in EngineBuiltins.Iterate(DetectionProfileStore.GetOrDefault(entry, "configs", new List<object?>())))
            {
                if (cfg is IReadOnlyDictionary<string, object?>)
                {
                    configs.Add(DetectionProfileStore.ConfigFromDict(cfg, EngineRepr.Str(DetectionProfileStore.Subscript(cfg, "id"))));
                }
            }

            var name = EngineRepr.Str(DetectionProfileStore.Subscript(entry, "name"));
            profiles.Add(DetectionProfileStore.ProfileFromDict(entry, name, configs));
        }

        return new DetectionProfileStore(profiles);
    }

    /// <summary>The <see cref="DetectionProfileStore.Summary"/> of the store at <paramref name="storePath"/>.</summary>
    public static OrderedDictionary<string, object?> Summary(string storePath)
        => StoreFromPayload(LoadJson(storePath)).Summary();

    /// <summary><see cref="DetectionProfileStore.DiffSummarySnapshots"/> of two summary files.</summary>
    public static OrderedDictionary<string, object?> Diff(string baselinePath, string currentPath)
    {
        var baseline = LoadJson(baselinePath);
        var current = LoadJson(currentPath);
        return DetectionProfileStore.DiffSummarySnapshots(baseline, current);
    }

    /// <summary>
    /// Attaches matching profile configs to each hunt hit. The hunt payload must be a JSON array (a string is also accepted);
    /// otherwise <see cref="InvalidDataException"/> (<c>Hunt payload must be a JSON array of hunt hits.</c>). Non-object items are skipped.
    /// </summary>
    public static OrderedDictionary<string, object?> HuntBridge(string storePath, string huntPath, IEnumerable<string?>? tags, string? root)
    {
        var storePayload = LoadJson(storePath);
        var huntsPayload = LoadJson(huntPath);
        // A list or a string is accepted; a dict is not (OrderedDictionary also implements IList).
        if (huntsPayload is IReadOnlyDictionary<string, object?> or not (IList or string))
        {
            throw new InvalidDataException("Hunt payload must be a JSON array of hunt hits.");
        }

        var store = StoreFromPayload(storePayload);
        var hunts = EngineBuiltins.Iterate(huntsPayload).OfType<IReadOnlyDictionary<string, object?>>();
        return BuildBridgePayload(store, hunts, tags?.ToList(), root);
    }

    /// <summary>
    /// A non-empty <c>relative_path</c>; otherwise from <c>path</c>: its name when there is no root or it is outside it, else the posix
    /// path relative to the root.
    /// </summary>
    public static string? ResolveRelativePath(IReadOnlyDictionary<string, object?> entry, string? root)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TryGetValue("relative_path", out var relative) && relative is string { Length: > 0 } relativeText)
        {
            return relativeText;
        }

        if (!entry.TryGetValue("path", out var pathValue) || pathValue is not string { Length: > 0 } pathText)
        {
            return null;
        }

        if (root is null)
        {
            return PathText.Name(pathText);
        }

        return LexicalPath.RelativeTo(pathText, root) ?? PathText.Name(pathText);
    }

    /// <summary>
    /// <c>{"items": [...]}</c>: per hit, <c>hunt</c> (the hit), <c>relative_path</c> and <c>profiles</c>, each match with <c>profile</c>,
    /// <c>config</c>, sorted <c>profile_tags</c>, <c>expected_format</c> and <c>expected_variant</c>.
    /// </summary>
    public static OrderedDictionary<string, object?> BuildBridgePayload(
        DetectionProfileStore store,
        IEnumerable<IReadOnlyDictionary<string, object?>> hunts,
        IEnumerable<string?>? tags,
        string? root)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hunts);
        var tagList = tags?.ToList();
        var items = new List<object?>();
        foreach (var entry in hunts)
        {
            var relative = ResolveRelativePath(entry, root);
            var matches = store.MatchingConfigs(tagList, relative);
            var profiles = matches.Select(match =>
            {
                var profileTags = match.Profile.Tags.ToList();
                profileTags.Sort(PathText.CompareCodePoints);
                return (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["profile"] = match.Profile.Name,
                    ["config"] = match.Config.Identifier,
                    ["profile_tags"] = profileTags.Cast<object?>().ToList(),
                    ["expected_format"] = match.Config.ExpectedFormat,
                    ["expected_variant"] = match.Config.ExpectedVariant,
                };
            }).ToList();

            items.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hunt"] = entry,
                ["relative_path"] = relative,
                ["profiles"] = profiles,
            });
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["items"] = items };
    }
}
