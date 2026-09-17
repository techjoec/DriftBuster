using System.Collections;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Remote;

/// <summary>The snapshot's detection, profile and hunt entries.</summary>
public static partial class CaptureRunner
{
    /// <summary>
    /// <c>_serialise_profile_config(binding)</c>: <c>profile</c> (name, description, sorted tags, a copy of its metadata) and
    /// <c>config</c> (id, path, path_glob, application, version, branch, sorted tags, expected_format, expected_variant, a copy of its
    /// metadata). Tags sort by code point.
    /// </summary>
    public static OrderedDictionary<string, object?> SerialiseProfileConfig(AppliedProfileConfig binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var profile = binding.Profile;
        var config = binding.Config;
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = profile.Name,
                ["description"] = profile.Description,
                ["tags"] = SortedText(profile.Tags),
                ["metadata"] = CopyMapping(profile.Metadata),
            },
            ["config"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = config.Identifier,
                ["path"] = config.Path,
                ["path_glob"] = config.PathGlob,
                ["application"] = config.Application,
                ["version"] = config.Version,
                ["branch"] = config.Branch,
                ["tags"] = SortedText(config.Tags),
                ["expected_format"] = config.ExpectedFormat,
                ["expected_variant"] = config.ExpectedVariant,
                ["metadata"] = CopyMapping(config.Metadata),
            },
        };
    }

    /// <summary><c>_relative_path(path, root)</c>: <c>path.relative_to(root).as_posix()</c> (<c>.</c> for the root itself), or the file name.</summary>
    public static string RelativePath(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        return LexicalPath.RelativeTo(path, root) ?? PathText.Name(path);
    }

    /// <summary>
    /// <c>_serialise_detection(entry, root)</c>: <see cref="DetectionMetadata.SummariseMetadata"/> of the match plus <c>path</c>,
    /// <c>relative_path</c> and one <see cref="SerialiseProfileConfig"/> entry per applied profile config.
    /// </summary>
    /// <exception cref="EngineValueException">The entry has no match (<c>Cannot serialise detection for paths without a match.</c>).</exception>
    public static OrderedDictionary<string, object?> SerialiseDetection(ProfiledDetection entry, string root)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Detection is null)
        {
            throw new EngineValueException("Cannot serialise detection for paths without a match.", nameof(entry));
        }

        var payload = DetectionPayload(entry.Detection);
        payload["path"] = entry.Path;
        payload["relative_path"] = RelativePath(entry.Path, root);
        payload["profiles"] = entry.Profiles.Select(object? (binding) => SerialiseProfileConfig(binding)).ToList();
        return payload;
    }

    /// <summary><c>_serialise_plain_detection(path, match, root)</c>: the match summary with <c>path</c>, <c>relative_path</c> and no profiles.</summary>
    public static OrderedDictionary<string, object?> SerialisePlainDetection(string path, DetectionMatch match, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(match);
        var payload = DetectionPayload(match);
        payload["path"] = path;
        payload["relative_path"] = RelativePath(path, root);
        payload["profiles"] = new List<object?>();
        return payload;
    }

    /// <summary>
    /// <c>_serialise_hunt_hit(hit, root)</c>: <c>rule</c> (name, description, token_name, keywords, pattern texts), <c>path</c>,
    /// <c>relative_path</c> (relative to the capture root), <c>line_number</c> and <c>excerpt</c>.
    /// </summary>
    public static OrderedDictionary<string, object?> SerialiseHuntHit(HuntFinding hit, string root)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = hit.Rule.Name,
                ["description"] = hit.Rule.Description,
                ["token_name"] = hit.Rule.TokenName,
                ["keywords"] = hit.Rule.Keywords.Cast<object?>().ToList(),
                ["patterns"] = hit.Rule.Patterns.Select(object? (pattern) => pattern.ToString()).ToList(),
            },
            ["path"] = hit.Path,
            ["relative_path"] = RelativePath(hit.Path, root),
            ["line_number"] = hit.LineNumber,
            ["excerpt"] = hit.Excerpt,
        };
    }

    /// <summary>
    /// <c>_normalise_summary(summary)</c>: mappings copied with <c>str()</c> keys, lists, tuples and sets turned into lists, recursively;
    /// every other value kept.
    /// </summary>
    public static object? NormaliseSummary(object? value) => value switch
    {
        string or byte[] => value,
        IReadOnlyDictionary<string, object?> mapping => mapping.Aggregate(
            new OrderedDictionary<string, object?>(StringComparer.Ordinal),
            (copy, pair) =>
            {
                copy[pair.Key] = NormaliseSummary(pair.Value);
                return copy;
            }),
        IDictionary dictionary => NormaliseDictionary(dictionary),
        IEnumerable items => items.Cast<object?>().Select(NormaliseSummary).ToList(),
        _ => value,
    };

    private static OrderedDictionary<string, object?> NormaliseDictionary(IDictionary dictionary)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var entries = dictionary.GetEnumerator();
        while (entries.MoveNext())
        {
            copy[EngineRepr.Str(entries.Key)] = NormaliseSummary(entries.Value);
        }

        return copy;
    }

    // dict(summarise_metadata(match)) with reasons as a JSON list.
    private static OrderedDictionary<string, object?> DetectionPayload(DetectionMatch match)
    {
        var payload = DetectionMetadata.SummariseMetadata(match);
        payload["reasons"] = match.Reasons.Cast<object?>().ToList();
        return payload;
    }

    private static List<object?> SortedText(IEnumerable<string> values)
    {
        var sorted = values.ToList();
        sorted.Sort(PathText.CompareCodePoints);
        return sorted.Cast<object?>().ToList();
    }

    // _ensure_mapping(data): dict(data or {}).
    private static OrderedDictionary<string, object?> CopyMapping(IReadOnlyDictionary<string, object?>? mapping)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping ?? new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            copy[key] = value;
        }

        return copy;
    }
}
