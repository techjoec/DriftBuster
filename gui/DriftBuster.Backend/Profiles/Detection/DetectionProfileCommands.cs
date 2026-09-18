using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// The library half of <c>profile_cli</c> (<c>driftbuster-profile</c>): JSON loading with friendly errors, the store builder with its
/// lenient fallback, and the <c>summary</c>, <c>diff</c> and <c>hunt-bridge</c> commands, each returning the payload the command
/// writes as JSON. Argument parsing, output files and exit codes belong to the console tool.
/// </summary>
public static class DetectionProfileCommands
{
    /// <summary>
    /// The <c>ProfileStore.from_dict</c> lookup <see cref="StoreFromPayload"/> makes (<c>getattr(ProfileStore, "from_dict", None)</c>);
    /// tests swap it, and null stands for the attribute being absent.
    /// </summary>
    internal static Func<object?, DetectionProfileStore>? FromDict { get; set; } = DetectionProfileStore.FromDict;

    /// <summary>
    /// <c>_load_json(path)</c>: the JSON value stored at <paramref name="path"/>, decoded as UTF-8. A read failure raises
    /// <see cref="IOException"/> (<c>Unable to read JSON payload from {path}: {reason}</c>), invalid JSON
    /// <see cref="InvalidDataException"/> (<c>Failed to parse JSON from {path}: ...</c>); bytes that are not UTF-8
    /// (<see cref="EngineUtf8.Decode"/>) and the decoder's limits (<see cref="EngineJson.TryLoadsOrRaiseLimits"/>) raise their own
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
    /// <c>_store_from_payload(payload)</c>: <see cref="DetectionProfileStore.FromDict"/>, or, when that raises, a lenient build that
    /// skips profile entries and configs that are not dicts and takes <c>str()</c> of each <c>id</c> and <c>name</c>.
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

    /// <summary><c>_handle_summary</c>: the <see cref="DetectionProfileStore.Summary"/> of the store payload at <paramref name="storePath"/>.</summary>
    public static OrderedDictionary<string, object?> Summary(string storePath)
        => StoreFromPayload(LoadJson(storePath)).Summary();

    /// <summary><c>_handle_diff</c>: <see cref="DetectionProfileStore.DiffSummarySnapshots"/> of the two summary files.</summary>
    public static OrderedDictionary<string, object?> Diff(string baselinePath, string currentPath)
    {
        var baseline = LoadJson(baselinePath);
        var current = LoadJson(currentPath);
        return DetectionProfileStore.DiffSummarySnapshots(baseline, current);
    }

    /// <summary>
    /// <c>_handle_hunt_bridge</c>: attaches the matching profile configs to each hunt hit. The hunt payload must be a JSON array
    /// (or a str, which Python also accepts as a sequence); anything else raises
    /// <see cref="InvalidDataException"/> (<c>Hunt payload must be a JSON array of hunt hits.</c>). Items that are not dicts are skipped.
    /// </summary>
    public static OrderedDictionary<string, object?> HuntBridge(string storePath, string huntPath, IEnumerable<string?>? tags, string? root)
    {
        var storePayload = LoadJson(storePath);
        var huntsPayload = LoadJson(huntPath);
        // isinstance(payload, Sequence): a list or a str; a dict (which OrderedDictionary also exposes as IList) is not one.
        if (huntsPayload is IReadOnlyDictionary<string, object?> or not (IList or string))
        {
            throw new InvalidDataException("Hunt payload must be a JSON array of hunt hits.");
        }

        var store = StoreFromPayload(storePayload);
        var hunts = EngineBuiltins.Iterate(huntsPayload).OfType<IReadOnlyDictionary<string, object?>>();
        return BuildBridgePayload(store, hunts, tags?.ToList(), root);
    }

    /// <summary>
    /// <c>_resolve_relative_path(entry, root)</c>: a non-empty str <c>relative_path</c>; otherwise, from a non-empty str
    /// <c>path</c>, its name when there is no root or the path is not under it, else the posix path relative to the root.
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
    /// <c>_build_bridge_payload</c>: <c>{"items": [...]}</c>, one item per hunt hit with <c>hunt</c> (the hit itself),
    /// <c>relative_path</c> and <c>profiles</c>, each match giving <c>profile</c>, <c>config</c>, sorted <c>profile_tags</c>,
    /// <c>expected_format</c> and <c>expected_variant</c>.
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
