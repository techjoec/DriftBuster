namespace DriftBuster.Backend.Reporting;

public static partial class HtmlReport
{
    /// <summary>
    /// <c>_format_safety_notice</c>: for a mapping, one segment per canonical side whose <c>truncated_bytes</c> is truthy, one diff
    /// segment when a <c>diff</c> mapping is present, and, when any segment exists, the truthy thresholds; empty otherwise.
    /// </summary>
    internal static string FormatSafetyNotice(object? safety)
    {
        if (!ReportValues.IsMapping(safety, out var limits))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        if (ReportValues.IsMapping(limits.GetValueOrDefault("canonical"), out var canonical))
        {
            foreach (var (label, info) in canonical)
            {
                if (!ReportValues.IsMapping(info, out var side) || !ReportValues.Truthy(side.GetValueOrDefault("truncated_bytes")))
                {
                    continue;
                }

                var piece = $"{label} canonical truncated {ReportValues.Str(side.GetValueOrDefault("truncated_bytes"))} bytes";
                var digest = side.GetValueOrDefault("digest");
                segments.Add(ReportValues.Truthy(digest) ? piece + $" (digest {ReportValues.Str(digest)})" : piece);
            }
        }

        if (ReportValues.IsMapping(limits.GetValueOrDefault("diff"), out var diffInfo))
        {
            segments.Add(DiffSegment(diffInfo));
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (ReportValues.IsMapping(limits.GetValueOrDefault("thresholds"), out var thresholds))
        {
            var parts = new List<string>();
            AddThreshold(parts, thresholds, "canonical_bytes", "canonical\u2264{0} bytes");
            AddThreshold(parts, thresholds, "diff_bytes", "diff\u2264{0} bytes");
            AddThreshold(parts, thresholds, "diff_lines", "diff\u2264{0} lines");
            if (parts.Count > 0)
            {
                segments.Add("thresholds: " + string.Join(", ", parts));
            }
        }

        return "Diff output truncated for safety: " + string.Join("; ", segments);
    }

    private static string DiffSegment(IReadOnlyDictionary<string, object?> diffInfo)
    {
        var truncatedLines = diffInfo.GetValueOrDefault("truncated_lines");
        var truncatedBytes = diffInfo.GetValueOrDefault("truncated_bytes");
        var parts = new List<string>();
        if (ReportValues.Truthy(truncatedLines))
        {
            parts.Add($"{ReportValues.Str(truncatedLines)} lines");
        }

        if (ReportValues.Truthy(truncatedBytes))
        {
            parts.Add($"{ReportValues.Str(truncatedBytes)} bytes");
        }

        var segment = parts.Count > 0 ? "diff truncated " + string.Join(" and ", parts) : "diff output truncated";
        var digest = diffInfo.GetValueOrDefault("digest");
        return ReportValues.Truthy(digest) ? segment + $" (digest {ReportValues.Str(digest)})" : segment;
    }

    private static void AddThreshold(List<string> parts, IReadOnlyDictionary<string, object?> thresholds, string key, string template)
    {
        var value = thresholds.GetValueOrDefault(key);
        if (ReportValues.Truthy(value))
        {
            parts.Add(template.Replace("{0}", ReportValues.Str(value), StringComparison.Ordinal));
        }
    }
}
