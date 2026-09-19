using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Remote;

/// <summary>The registry scan summaries a capture manifest embeds.</summary>
public static partial class CaptureRunner
{
    /// <summary>Each path expanded (<c>~</c>) and resolved, then <see cref="SummariseRegistryScan"/>.</summary>
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
    /// <c>file</c> (name), <c>path</c>, <c>token</c>, <c>roots</c> and <c>requested_roots</c> (entries with non-empty hive and path, as
    /// <c>"{hive} \ {path}"</c> plus <c>" (view {view})"</c> when set) and <c>hit_count</c> (absent counts as zero).
    /// </summary>
    /// <remarks>
    /// UTF-8 JSON; invalid JSON throws <see cref="InvalidDataException"/> (<c>Failed to parse registry scan {path}: invalid JSON document</c>),
    /// as do wrong shapes, non-UTF-8 bytes and decoder limits; a directory throws <see cref="UnauthorizedAccessException"/>.
    /// </remarks>
    public static OrderedDictionary<string, object?> SummariseRegistryScan(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var payload = EngineJson.TryLoadsOrRaiseLimits(ReadUtf8Text(path), out var value)
            ? value
            : throw new InvalidDataException($"Failed to parse registry scan {path}: invalid JSON document");

        // Absent or falsy counts as an empty list.
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

    // Labels of the entries that have a hive and a path.
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
