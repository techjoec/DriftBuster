using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary><c>ProfileStore.from_dict</c>, <c>to_dict</c> and <c>summary</c> over Python-shaped JSON values.</summary>
public sealed partial class DetectionProfileStore
{
    /// <summary>
    /// <c>ProfileStore.from_dict(payload)</c> over a <see cref="PythonJson"/> value: <c>profiles</c> (default empty) holds entries
    /// with <c>name</c>, <c>description</c>, <c>tags</c>, <c>configs</c> and <c>metadata</c>; each config has <c>id</c>,
    /// <c>path</c>, <c>path_glob</c>, <c>application</c>, <c>version</c>, <c>branch</c>, <c>tags</c>, <c>expected_format</c>,
    /// <c>expected_variant</c> and <c>metadata</c>. Python's errors are raised for a payload or entry that is not a dict
    /// (<c>AttributeError</c>, <see cref="PythonAttributeException"/>), a config that cannot be subscripted (<c>TypeError</c>), a
    /// missing <c>id</c> or <c>name</c> (<c>KeyError</c>), a <c>path</c> or <c>path_glob</c> that is not a str, tags and metadata
    /// Python cannot convert, and the store's duplicate checks.
    /// </summary>
    /// <remarks>
    /// The typed profile holds str fields. A list or dict <c>id</c> or <c>name</c> raises <c>TypeError: unhashable type</c> at the
    /// point registration first hashes it, as in Python; any other value that is not a str (a number, a bool, None, or a list or dict in the other
    /// text fields) is stored as its <c>str()</c> text, which is what Python compares and formats it as.
    /// </remarks>
    public static DetectionProfileStore FromDict(object? payload)
    {
        var profiles = new List<DetectionProfile>();
        var unhashable = new Dictionary<object, PythonTypeException>(ReferenceEqualityComparer.Instance);
        foreach (var entry in PythonBuiltins.Iterate(GetOrDefault(payload, "profiles", EmptyList)))
        {
            var configs = new List<DetectionProfileConfig>();
            foreach (var cfg in PythonBuiltins.Iterate(GetOrDefault(entry, "configs", EmptyList)))
            {
                var identifierValue = Subscript(cfg, "id");
                var config = ConfigFromDict(cfg, PythonRepr.Str(identifierValue));
                NoteUnhashable(unhashable, config, identifierValue);
                configs.Add(config);
            }

            var nameValue = Subscript(entry, "name");
            var profile = ProfileFromDict(entry, PythonRepr.Str(nameValue), configs);
            NoteUnhashable(unhashable, profile, nameValue);
            profiles.Add(profile);
        }

        var store = new DetectionProfileStore { _unhashable = unhashable };
        foreach (var profile in profiles)
        {
            store.RegisterProfile(profile);
        }

        store._unhashable = null;
        return store;
    }

    // A list or dict name or id is kept as its str() text and raises TypeError where registration first hashes it.
    private static void NoteUnhashable(Dictionary<object, PythonTypeException> unhashable, object owner, object? value)
    {
        if (value is IList or IReadOnlyDictionary<string, object?>)
        {
            unhashable[owner] = new PythonTypeException($"unhashable type: '{PythonBuiltins.TypeName(value)}'", nameof(value));
        }
    }

    private static readonly List<object?> EmptyList = [];

    /// <summary>
    /// <c>ConfigurationProfile(name=..., description=entry.get("description"), tags=entry.get("tags"), configs=...,
    /// metadata=entry.get("metadata", {}))</c> for a dict entry whose name has been read.
    /// </summary>
    internal static DetectionProfile ProfileFromDict(object? entry, string name, IEnumerable<DetectionProfileConfig> configs)
    {
        var description = OptionalText(PythonBuiltins.Get(entry, "description"));
        var tagsValue = PythonBuiltins.Get(entry, "tags");
        var metadataValue = GetOrDefault(entry, "metadata", null);
        return new DetectionProfile(
            name,
            description: description,
            tags: ProfileTags.NormalizeValue(tagsValue),
            configs: configs,
            metadata: ProfileMetadata.FromValue(metadataValue));
    }

    /// <summary>
    /// <c>ProfileConfig(identifier=..., path=cfg.get("path"), ...)</c> for a dict config whose id has been read, with
    /// <c>__post_init__</c>'s checks in its order: tags, metadata, path, path_glob.
    /// </summary>
    internal static DetectionProfileConfig ConfigFromDict(object? cfg, string identifier)
    {
        var path = PythonBuiltins.Get(cfg, "path");
        var pathGlob = PythonBuiltins.Get(cfg, "path_glob");
        var application = OptionalText(PythonBuiltins.Get(cfg, "application"));
        var version = OptionalText(PythonBuiltins.Get(cfg, "version"));
        var branch = OptionalText(PythonBuiltins.Get(cfg, "branch"));
        var tagsValue = PythonBuiltins.Get(cfg, "tags");
        var expectedFormat = OptionalText(PythonBuiltins.Get(cfg, "expected_format"));
        var expectedVariant = OptionalText(PythonBuiltins.Get(cfg, "expected_variant"));
        var metadataValue = GetOrDefault(cfg, "metadata", null);

        var tags = ProfileTags.NormalizeValue(tagsValue);
        var metadata = ProfileMetadata.FromValue(metadataValue);
        return new DetectionProfileConfig(
            identifier,
            path: PurePosixPathText(path),
            pathGlob: PurePosixPathText(pathGlob),
            application: application,
            version: version,
            branch: branch,
            tags: tags,
            expectedFormat: expectedFormat,
            expectedVariant: expectedVariant,
            metadata: metadata);
    }

    /// <summary><c>mapping.get(key, default)</c>: <c>AttributeError</c> when <paramref name="value"/> is not a dict.</summary>
    internal static object? GetOrDefault(object? value, string key, object? fallback)
    {
        _ = PythonBuiltins.Get(value, key);
        return ((IReadOnlyDictionary<string, object?>)value!).TryGetValue(key, out var item) ? item : fallback;
    }

    /// <summary><c>value[key]</c> for a str key: <c>KeyError</c> on a dict without it, Python's <c>TypeError</c> on anything else.</summary>
    internal static object? Subscript(object? value, string key)
    {
        return value switch
        {
            IReadOnlyDictionary<string, object?> mapping => mapping.TryGetValue(key, out var item)
                ? item
                : throw new KeyNotFoundException(PythonRepr.StrRepr(key)),
            string => throw new PythonTypeException("string indices must be integers, not 'str'", nameof(value)),
            IList => throw new PythonTypeException("list indices must be integers or slices, not str", nameof(value)),
            _ => throw new PythonTypeException($"'{PythonBuiltins.TypeName(value)}' object is not subscriptable", nameof(value)),
        };
    }

    internal static string? OptionalText(object? value) => value is null ? null : PythonRepr.Str(value);

    // PurePosixPath(value) accepts only str (or None, which the dataclass never normalises).
    internal static string? PurePosixPathText(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => throw new PythonTypeException(
            $"argument should be a str or an os.PathLike object where __fspath__ returns a str, not '{PythonBuiltins.TypeName(value)}'",
            nameof(value)),
    };

    /// <summary><c>to_dict()</c>: every profile in registration order with its configs, tags sorted by code point.</summary>
    public OrderedDictionary<string, object?> ToDict()
    {
        var profiles = new List<object?>();
        foreach (var profile in _profiles.Values)
        {
            var configs = profile.Configs.Select(config => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = config.Identifier,
                ["path"] = config.Path,
                ["path_glob"] = config.PathGlob,
                ["application"] = config.Application,
                ["version"] = config.Version,
                ["branch"] = config.Branch,
                ["tags"] = SortedTags(config.Tags),
                ["expected_format"] = config.ExpectedFormat,
                ["expected_variant"] = config.ExpectedVariant,
                ["metadata"] = CopyMapping(config.Metadata),
            }).ToList();

            profiles.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = profile.Name,
                ["description"] = profile.Description,
                ["tags"] = SortedTags(profile.Tags),
                ["metadata"] = CopyMapping(profile.Metadata),
                ["configs"] = configs,
            });
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["profiles"] = profiles };
    }

    /// <summary>
    /// <c>summary()</c>: <c>total_profiles</c>, <c>total_configs</c> and one entry per profile in name order (by code point) with
    /// <c>name</c>, <c>description</c>, sorted <c>tags</c>, <c>config_count</c> and <c>config_ids</c> in config order.
    /// </summary>
    public OrderedDictionary<string, object?> Summary()
    {
        var orderedNames = _profiles.Keys.ToList();
        orderedNames.Sort(PathText.CompareCodePoints);
        var entries = new List<object?>();
        var totalConfigs = 0;
        foreach (var name in orderedNames)
        {
            var profile = _profiles[name];
            var configIds = profile.Configs.Select(config => (object?)config.Identifier).ToList();
            totalConfigs += configIds.Count;
            entries.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = profile.Name,
                ["description"] = profile.Description,
                ["tags"] = SortedTags(profile.Tags),
                ["config_count"] = configIds.Count,
                ["config_ids"] = configIds,
            });
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_profiles"] = orderedNames.Count,
            ["total_configs"] = totalConfigs,
            ["profiles"] = entries,
        };
    }

    private static List<object?> SortedTags(IReadOnlySet<string> tags)
    {
        var sorted = tags.ToList();
        sorted.Sort(PathText.CompareCodePoints);
        return sorted.Cast<object?>().ToList();
    }

    private static OrderedDictionary<string, object?> CopyMapping(IReadOnlyDictionary<string, object?> mapping)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping)
        {
            copy[key] = value;
        }

        return copy;
    }
}
