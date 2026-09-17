using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Reporting;

/// <summary><c>driftbuster.reporting._metadata</c>: normalised detection payloads shared by the reporting adapters.</summary>
public static class DetectionPayloads
{
    /// <summary>
    /// <c>iter_detection_payloads</c>: <see cref="DetectionMetadata.SummariseMetadata"/> for each match, its metadata copied and
    /// updated with <paramref name="extraMetadata"/> (whose values are not made JSON-safe). The match is never changed.
    /// </summary>
    public static IEnumerable<OrderedDictionary<string, object?>> Iterate(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return IterateCore(matches, extraMetadata);
    }

    private static IEnumerable<OrderedDictionary<string, object?>> IterateCore(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? extraMetadata)
    {
        var runMetadata = ReportValues.Copy(extraMetadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        foreach (var match in matches)
        {
            var summary = ReportValues.Copy(DetectionMetadata.SummariseMetadata(match));
            var metadataMap = ReportValues.IsMapping(summary["metadata"], out var metadata)
                ? ReportValues.Copy(metadata)
                : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in runMetadata)
            {
                metadataMap[key] = value;
            }

            summary["metadata"] = metadataMap;
            yield return summary;
        }
    }
}
