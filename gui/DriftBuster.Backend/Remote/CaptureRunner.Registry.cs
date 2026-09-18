using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Remote;

/// <summary>The registry scan summaries a capture manifest embeds.</summary>
public static partial class CaptureRunner
{
    /// <summary>
    /// <c>_load_registry_scan_summaries(paths)</c>: each path expanded (<c>~</c>) and resolved, then <see cref="SummariseRegistryScan"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">A path does not exist (<c>registry scan file not found: {path}</c>).</exception>
    public static IReadOnlyList<OrderedDictionary<string, object?>> LoadRegistryScanSummaries(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var summaries = new List<OrderedDictionary<string, object?>>();
        foreach (var entry in paths)
        {
            var scanPath = EnginePath.Resolve(EnginePath.ExpandUser(entry));
            if (!RunProfileStore.Exists(scanPath))
            {
                throw new FileNotFoundException($"registry scan file not found: {scanPath}", scanPath);
            }

            summaries.Add(SummariseRegistryScan(scanPath));
        }

        return summaries;
    }

    /// <summary>
    /// <c>_summarise_registry_scan(path)</c>: <c>file</c> (the name), <c>path</c>, <c>token</c> (as stored), <c>roots</c> and
    /// <c>requested_roots</c> (each mapping entry whose stripped <c>str()</c> hive and path are both non-empty, as
    /// <c>"{hive} \ {path}"</c> plus <c>" (view {view})"</c> for a truthy view) and <c>hit_count</c> (<c>len()</c> of <c>hits</c>, an
    /// absent or falsy value counting as empty).
    /// </summary>
    /// <remarks>
    /// The file is read as <c>path.read_text(encoding="utf-8")</c> and decoded as <c>json.loads</c> decodes it. Text that is not a JSON
    /// document raises <see cref="InvalidDataException"/> (<c>Failed to parse registry scan {path}: invalid JSON document</c>;
    /// <see cref="EngineJson"/> reports no decoder reason). A payload or root list that is not a mapping where <c>.get</c> is called, roots
    /// that cannot be iterated, hits without a length, bytes that are not UTF-8 and the decoder's limits raise
    /// <see cref="InvalidDataException"/>; a directory raises <see cref="UnauthorizedAccessException"/>.
    /// </remarks>
    public static OrderedDictionary<string, object?> SummariseRegistryScan(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var payload = EngineJson.TryLoadsOrRaiseLimits(ReadUtf8Text(path), out var value)
            ? value
            : throw new InvalidDataException($"Failed to parse registry scan {path}: invalid JSON document");

        // payload.get(key, []) or []: an absent or falsy value counts as an empty list.
        var roots = RootLabels(DetectionProfileStore.GetOrDefault(payload, "roots", null));
        var requested = RootLabels(DetectionProfileStore.GetOrDefault(payload, "requested_roots", null));
        var token = EngineBuiltins.Get(payload, "token");
        var hits = DetectionProfileStore.GetOrDefault(payload, "hits", null);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["file"] = PathText.Name(path),
            ["path"] = path,
            ["token"] = token,
            ["roots"] = roots,
            ["requested_roots"] = requested,
            ["hit_count"] = EngineBuiltins.IsTruthy(hits) ? EngineBuiltins.Len(hits) : 0,
        };
    }

    // for entry in value or []: the labels of the mapping entries with a hive and a path.
    private static List<object?> RootLabels(object? value)
    {
        var labels = new List<object?>();
        if (!EngineBuiltins.IsTruthy(value))
        {
            return labels;
        }

        foreach (var entry in EngineBuiltins.Iterate(value))
        {
            if (entry is not IReadOnlyDictionary<string, object?> mapping)
            {
                continue;
            }

            var hive = EngineText.Strip(EngineRepr.Str(DetectionProfileStore.GetOrDefault(mapping, "hive", string.Empty)));
            var keyPath = EngineText.Strip(EngineRepr.Str(DetectionProfileStore.GetOrDefault(mapping, "path", string.Empty)));
            if (hive.Length == 0 || keyPath.Length == 0)
            {
                continue;
            }

            var view = EngineBuiltins.Get(mapping, "view");
            var label = $"{hive} \\ {keyPath}";
            labels.Add(EngineBuiltins.IsTruthy(view) ? $"{label} (view {EngineRepr.Str(view)})" : label);
        }

        return labels;
    }
}
