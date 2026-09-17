using System.Collections;

namespace DriftBuster.Backend.Reporting;

public static partial class DetectionSummary
{
    // The catalog_remediations loop: entries that are not mappings are skipped; a truthy id or summary becomes its str(); the id joins
    // the variant's ids once; the record is keyed by the id, else the summary, and fills its falsy id, category and documentation from
    // later entries that carry them.
    private static void IndexRemediations(
        IEnumerable remediations,
        VariantBucket variantBucket,
        Dictionary<string, RemediationRecord> remediationIndex,
        string formatName,
        string variantName)
    {
        foreach (var item in remediations)
        {
            if (!ReportValues.IsMapping(item, out var entry))
            {
                continue;
            }

            var remediationId = entry.GetValueOrDefault("id");
            var remediationSummary = entry.GetValueOrDefault("summary");
            var hasId = ReportValues.Truthy(remediationId);
            if (hasId)
            {
                remediationId = ReportValues.Str(remediationId);
            }

            if (ReportValues.Truthy(remediationSummary))
            {
                remediationSummary = ReportValues.Str(remediationSummary);
            }

            if (hasId && !variantBucket.RemediationIds.Contains((string)remediationId!, StringComparer.Ordinal))
            {
                variantBucket.RemediationIds.Add((string)remediationId!);
            }

            var key = hasId ? remediationId : remediationSummary;
            if (!ReportValues.Truthy(key))
            {
                continue;
            }

            if (!remediationIndex.TryGetValue((string)key!, out var record))
            {
                record = new RemediationRecord(remediationId, entry.GetValueOrDefault("category"), remediationSummary, entry.GetValueOrDefault("documentation"));
                remediationIndex[(string)key!] = record;
            }

            FillRecord(record, entry, remediationId, hasId);
            record.Formats.Add(formatName);
            if (!string.Equals(variantName, NoVariant, StringComparison.Ordinal))
            {
                record.Variants.Add(variantName);
            }
        }
    }

    private static void FillRecord(RemediationRecord record, IReadOnlyDictionary<string, object?> entry, object? remediationId, bool hasId)
    {
        if (hasId && !ReportValues.Truthy(record.Id))
        {
            record.Id = remediationId;
        }

        var category = entry.GetValueOrDefault("category");
        if (ReportValues.Truthy(category) && !ReportValues.Truthy(record.Category))
        {
            record.Category = category;
        }

        var documentation = entry.GetValueOrDefault("documentation");
        if (ReportValues.Truthy(documentation) && !ReportValues.Truthy(record.Documentation))
        {
            record.Documentation = documentation;
        }
    }

    private static List<object?> FormatRemediations(Dictionary<string, RemediationRecord> remediationIndex)
    {
        var formatted = new List<object?>();
        foreach (var key in SortedStrings(remediationIndex.Keys))
        {
            var entry = remediationIndex[key];
            formatted.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = entry.Id,
                ["category"] = entry.Category,
                ["summary"] = entry.Summary,
                ["documentation"] = entry.Documentation,
                ["formats"] = SortedList(entry.Formats),
                ["variants"] = SortedList(entry.Variants),
            });
        }

        return formatted;
    }

    private sealed class RemediationRecord(object? id, object? category, object? summary, object? documentation)
    {
        public object? Id { get; set; } = id;

        public object? Category { get; set; } = category;

        public object? Summary { get; } = summary;

        public object? Documentation { get; set; } = documentation;

        public HashSet<string> Formats { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Variants { get; } = new(StringComparer.Ordinal);
    }
}
