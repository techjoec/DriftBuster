using System.Collections;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Reporting;

/// <summary>Detections grouped by format and variant with severity and remediation indexes.</summary>
public static partial class DetectionSummary
{
    private const string NoVariant = "\u2014";

    /// <summary>
    /// <c>summarise_detections</c>: <c>total_matches</c>, <c>unique_formats</c>, <c>unique_variants</c>, <c>severity_counts</c> (sorted
    /// by severity), <c>formats</c> (by format, each with its variants by name, a missing or empty variant grouped as <c>—</c> and
    /// reported as null) and <c>remediations</c> (by id, or by summary when the id is falsy). Strings order by code point.
    /// </summary>
    public static OrderedDictionary<string, object?> Summarise(IEnumerable<DetectionMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var totalMatches = 0;
        var formatIndex = new Dictionary<string, FormatBucket>(StringComparer.Ordinal);
        var severityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var remediationIndex = new Dictionary<string, RemediationRecord>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            totalMatches++;
            var payload = DetectionMetadata.SummariseMetadata(match);
            var formatName = ReportValues.Str(ReportValues.Truthy(payload["format"]) ? payload["format"] : "unknown");
            var rawVariant = payload["variant"];
            var variantName = ReportValues.Truthy(rawVariant) ? ReportValues.Str(rawVariant) : NoVariant;
            ReportValues.IsMapping(payload["metadata"], out var metadataMap);

            if (!formatIndex.TryGetValue(formatName, out var formatBucket))
            {
                formatBucket = new FormatBucket();
                formatIndex[formatName] = formatBucket;
            }

            formatBucket.Total++;
            if (!formatBucket.Variants.TryGetValue(variantName, out var variantBucket))
            {
                variantBucket = new VariantBucket(string.Equals(variantName, NoVariant, StringComparison.Ordinal) ? null : variantName);
                formatBucket.Variants[variantName] = variantBucket;
            }

            AddToVariant(variantBucket, payload, metadataMap, severityCounts);
            if (metadataMap.TryGetValue("catalog_remediations", out var remediations) && IsSequence(remediations))
            {
                IndexRemediations((IEnumerable)remediations!, variantBucket, remediationIndex, formatName, variantName);
            }
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_matches"] = totalMatches,
            ["unique_formats"] = formatIndex.Count,
            ["unique_variants"] = formatIndex.Values.Sum(entry => entry.Variants.Count),
            ["severity_counts"] = SortedCounts(severityCounts),
            ["formats"] = FormatFormats(formatIndex),
            ["remediations"] = FormatRemediations(remediationIndex),
        };
    }

    // _is_sequence_of_mappings: a Sequence that is not str or bytes (the JSON-safe metadata holds lists for every sequence).
    private static bool IsSequence(object? value) => value is IList and not byte[];

    private static void AddToVariant(
        VariantBucket bucket,
        OrderedDictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> metadataMap,
        Dictionary<string, int> severityCounts)
    {
        bucket.Count++;
        bucket.MaxConfidence = ReportValues.Max(bucket.MaxConfidence, ReportValues.FloatOrZero(payload["confidence"]));
        foreach (var reason in ReportValues.Iterate(payload["reasons"]))
        {
            bucket.Reasons.Add(ReportValues.Str(reason));
        }

        foreach (var key in metadataMap.Keys)
        {
            bucket.MetadataKeys.Add(key);
        }

        if (metadataMap.TryGetValue("catalog_severity", out var severity) && severity is string { Length: > 0 } severityText)
        {
            severityCounts[severityText] = severityCounts.GetValueOrDefault(severityText) + 1;
            bucket.Severity ??= severityText;
        }

        if (metadataMap.TryGetValue("catalog_severity_hint", out var hint) && hint is string { Length: > 0 } hintText)
        {
            bucket.SeverityHint ??= hintText;
        }
    }

    private static OrderedDictionary<string, object?> SortedCounts(Dictionary<string, int> counts)
    {
        var sorted = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in SortedStrings(counts.Keys))
        {
            sorted[key] = counts[key];
        }

        return sorted;
    }

    private static List<object?> FormatFormats(Dictionary<string, FormatBucket> formatIndex)
    {
        var formatted = new List<object?>();
        foreach (var formatName in SortedStrings(formatIndex.Keys))
        {
            var entry = formatIndex[formatName];
            var variants = new List<object?>();
            foreach (var variantName in SortedStrings(entry.Variants.Keys))
            {
                var bucket = entry.Variants[variantName];
                variants.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["variant"] = bucket.Variant,
                    ["count"] = bucket.Count,
                    ["max_confidence"] = bucket.MaxConfidence,
                    ["reasons"] = SortedList(bucket.Reasons),
                    ["metadata_keys"] = SortedList(bucket.MetadataKeys),
                    ["severity"] = bucket.Severity,
                    ["severity_hint"] = bucket.SeverityHint,
                    ["remediation_ids"] = SortedList(bucket.RemediationIds),
                });
            }

            formatted.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["format"] = formatName,
                ["total"] = entry.Total,
                ["variants"] = variants,
            });
        }

        return formatted;
    }

    private static List<string> SortedStrings(IEnumerable<string> items)
        => items.Order(Comparer<string>.Create(PathText.CompareCodePoints)).ToList();

    private static List<object?> SortedList(IEnumerable<string> items) => SortedStrings(items).Cast<object?>().ToList();

    private sealed class FormatBucket
    {
        public int Total { get; set; }

        public Dictionary<string, VariantBucket> Variants { get; } = new(StringComparer.Ordinal);
    }

    private sealed class VariantBucket(string? variant)
    {
        public string? Variant { get; } = variant;

        public int Count { get; set; }

        public double MaxConfidence { get; set; }

        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MetadataKeys { get; } = new(StringComparer.Ordinal);

        public string? Severity { get; set; }

        public string? SeverityHint { get; set; }

        public List<string> RemediationIds { get; } = [];
    }
}
