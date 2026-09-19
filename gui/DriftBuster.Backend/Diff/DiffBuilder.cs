using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// Unified and binary diffs, their rendering, and the per-result and aggregate summaries and payloads.
/// </summary>
public static class DiffBuilder
{
    /// <summary>UTC clock for summary timestamps (test seam).</summary>
    internal static Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Both sides canonicalised for <paramref name="contentType"/> (unknown types throw <see cref="ArgumentException"/>), split into
    /// lines, redacted per line when a redactor resolves, diffed (<see cref="LineDiff"/>, <see cref="UnifiedDiffWriter"/>) with
    /// <paramref name="contextLines"/> of context, counted, and clamped by <see cref="DiffSafetyLimits"/>.
    /// </summary>
    public static DiffArtifact BuildUnifiedDiff(
        string before,
        string after,
        string contentType = "text",
        string fromLabel = "before",
        string toLabel = "after",
        string? label = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        int contextLines = 3)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        if (!Canonicaliser.IsSupported(contentType))
        {
            throw Canonicaliser.UnsupportedContentType(contentType);
        }

        var canonicalBefore = Canonicaliser.Canonicalise(before, contentType);
        var canonicalAfter = Canonicaliser.Canonicalise(after, contentType);

        var activeRedactor = RedactionFilter.Resolve(redactor, maskTokens, placeholder);
        var resultPlaceholder = activeRedactor?.Placeholder ?? placeholder;
        var beforeLines = ApplyRedaction(TextLines.SplitLines(canonicalBefore), activeRedactor);
        var afterLines = ApplyRedaction(TextLines.SplitLines(canonicalAfter), activeRedactor);
        var redactionCounts = activeRedactor?.Stats();
        var changes = LineDiff.Compare(beforeLines, afterLines);
        var diffText = string.Join("\n", UnifiedDiffWriter.Lines(beforeLines, afterLines, changes, fromLabel, toLabel, contextLines, lineTerm: string.Empty));
        var stats = LineDiff.CalculateStats(changes);

        IReadOnlyList<string>? maskList = null;
        if (activeRedactor is not null)
        {
            maskList = activeRedactor.OrderedTokens.Count > 0 ? activeRedactor.OrderedTokens.ToArray() : null;
        }
        else if (maskTokens is not null)
        {
            maskList = maskTokens.ToArray();
        }

        var (safeBefore, safeAfter, safeDiff, safetyLimits) = DiffSafetyLimits.Enforce(canonicalBefore, canonicalAfter, diffText);
        return new DiffArtifact
        {
            CanonicalBefore = safeBefore,
            CanonicalAfter = safeAfter,
            Diff = safeDiff,
            Stats = stats,
            ContentType = contentType,
            FromLabel = fromLabel,
            ToLabel = toLabel,
            Label = label,
            MaskTokens = maskList,
            Placeholder = resultPlaceholder,
            ContextLines = contextLines,
            RedactionCounts = redactionCounts,
            BinaryEvidence = null,
            SafetyLimits = safetyLimits,
        };
    }

    private static List<string> ApplyRedaction(IReadOnlyList<string> lines, RedactionFilter? redactor)
        => redactor is null ? lines.ToList() : lines.Select(redactor.Apply).ToList();

    /// <summary>The diff text of <see cref="BuildUnifiedDiff"/>.</summary>
    public static string RenderUnifiedDiff(
        string before,
        string after,
        string contentType = "text",
        string fromLabel = "before",
        string toLabel = "after",
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        int contextLines = 3)
        => BuildUnifiedDiff(before, after, contentType, fromLabel, toLabel, null, redactor, maskTokens, placeholder, contextLines).Diff;

    /// <summary>
    /// Digests stand in for the payloads; the diff names the label (default <c>binary</c>), both sizes and digests, and the signed
    /// byte delta when sizes differ.
    /// </summary>
    public static DiffArtifact BuildBinaryDiff(
        byte[] before,
        byte[] after,
        string fromLabel = "before",
        string toLabel = "after",
        string? label = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeDigest = DiffSafetyLimits.DigestBytes(before);
        var afterDigest = DiffSafetyLimits.DigestBytes(after);
        var evidence = new BinarySegmentEvidence
        {
            Label = string.IsNullOrEmpty(label) ? "binary" : label,
            BeforeSize = before.Length,
            AfterSize = after.Length,
            BeforeDigest = beforeDigest,
            AfterDigest = afterDigest,
            Changed = !before.AsSpan().SequenceEqual(after),
            Reason = reason,
        };
        var delta = after.Length - before.Length;
        var summaryLines = new List<string>
        {
            $"binary:{evidence.Label}",
            string.Create(CultureInfo.InvariantCulture, $"- before size={evidence.BeforeSize} digest={beforeDigest}"),
            string.Create(CultureInfo.InvariantCulture, $"+ after size={evidence.AfterSize} digest={afterDigest}"),
        };
        if (delta != 0)
        {
            summaryLines.Add("\u0394 bytes: " + delta.ToString("+0;-0", CultureInfo.InvariantCulture));
        }

        return new DiffArtifact
        {
            CanonicalBefore = beforeDigest,
            CanonicalAfter = afterDigest,
            Diff = string.Join("\n", summaryLines),
            Stats = new DiffStats(0, 0, evidence.Changed ? 1 : 0),
            ContentType = "binary",
            FromLabel = fromLabel,
            ToLabel = toLabel,
            Label = label,
            MaskTokens = null,
            Placeholder = RedactionFilter.DefaultPlaceholder,
            ContextLines = 0,
            RedactionCounts = null,
            BinaryEvidence = [evidence],
        };
    }

    public static DiffResultSummary SummariseDiffResult(
        DiffArtifact result,
        IReadOnlyList<string>? versions = null,
        string? baselineName = null,
        string? comparisonName = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var comparison = BuildComparisonSummary(result, baselineName, comparisonName);
        return NewSummary(versions, [comparison]);
    }

    /// <summary>
    /// One comparison per result; name lists, when given, must match the results in length. No results or a length mismatch throws
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static DiffResultSummary SummariseDiffResults(
        IReadOnlyList<DiffArtifact> results,
        IReadOnlyList<string>? versions = null,
        IReadOnlyList<string?>? baselineNames = null,
        IReadOnlyList<string?>? comparisonNames = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("results must not be empty.", nameof(results));
        }

        if (baselineNames is not null && baselineNames.Count != results.Count)
        {
            throw new ArgumentException("baseline_names length must match results.", nameof(baselineNames));
        }

        if (comparisonNames is not null && comparisonNames.Count != results.Count)
        {
            throw new ArgumentException("comparison_names length must match results.", nameof(comparisonNames));
        }

        var comparisons = results
            .Select((result, index) => BuildComparisonSummary(result, baselineNames?[index], comparisonNames?[index]))
            .ToArray();
        return NewSummary(versions, comparisons);
    }

    private static DiffResultSummary NewSummary(IReadOnlyList<string>? versions, DiffComparisonSummary[] comparisons)
    {
        // Microsecond precision.
        var now = UtcNow().ToUniversalTime();
        return new DiffResultSummary
        {
            GeneratedAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero),
            Versions = versions?.ToArray() ?? [],
            ComparisonCount = comparisons.Length,
            Comparisons = comparisons,
        };
    }

    internal static DiffComparisonSummary BuildComparisonSummary(DiffArtifact result, string? baselineName, string? comparisonName)
    {
        var stats = result.Stats;
        var redactionCounts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (token, count) in (result.RedactionCounts ?? new Dictionary<string, int>(StringComparer.Ordinal)).OrderBy(pair => pair.Key, Comparer<string>.Create(PathText.CompareCodePoints)))
        {
            redactionCounts[token] = count;
        }

        return new DiffComparisonSummary
        {
            From = result.FromLabel,
            To = result.ToLabel,
            Plan = new DiffPlanSummary
            {
                ContentType = result.ContentType,
                FromLabel = result.FromLabel,
                ToLabel = result.ToLabel,
                Label = result.Label,
                MaskTokens = result.MaskTokens?.ToArray() ?? [],
                Placeholder = result.Placeholder,
                ContextLines = result.ContextLines,
                RedactionCounts = redactionCounts,
                BinaryEvidence = result.BinaryEvidence?.ToArray() ?? [],
                SafetyLimits = result.SafetyLimits,
            },
            Metadata = new DiffMetadataSummary
            {
                ContentType = result.ContentType,
                ContextLines = result.ContextLines,
                BaselineName = string.IsNullOrEmpty(baselineName) ? result.FromLabel : baselineName,
                ComparisonName = string.IsNullOrEmpty(comparisonName) ? result.ToLabel : comparisonName,
            },
            Summary = new DiffChangeSummary
            {
                BeforeDigest = DiffSafetyLimits.Digest(result.CanonicalBefore),
                AfterDigest = DiffSafetyLimits.Digest(result.CanonicalAfter),
                DiffDigest = DiffSafetyLimits.Digest($"{result.CanonicalBefore}\n---\n{result.CanonicalAfter}"),
                BeforeLines = TextLines.SplitLines(result.CanonicalBefore).Count,
                AfterLines = TextLines.SplitLines(result.CanonicalAfter).Count,
                AddedLines = stats.AddedLines,
                RemovedLines = stats.RemovedLines,
                ChangedLines = stats.ChangedLines,
            },
        };
    }

    /// <summary>The JSON-ready summary, keys in a fixed order.</summary>
    public static OrderedDictionary<string, object?> DiffSummaryToPayload(DiffResultSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generated_at"] = IsoFormat(summary.GeneratedAt),
            ["versions"] = summary.Versions.Cast<object?>().ToList(),
            ["comparison_count"] = summary.ComparisonCount,
            ["comparisons"] = summary.Comparisons.Select(ComparisonPayload).Cast<object?>().ToList(),
        };
    }

    private static OrderedDictionary<string, object?> ComparisonPayload(DiffComparisonSummary comparison)
    {
        var plan = comparison.Plan;
        var redactionCounts = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (token, count) in plan.RedactionCounts ?? [])
        {
            redactionCounts[token] = count;
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = comparison.From,
            ["to"] = comparison.To,
            ["plan"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["content_type"] = plan.ContentType,
                ["from_label"] = plan.FromLabel,
                ["to_label"] = plan.ToLabel,
                ["label"] = plan.Label,
                ["mask_tokens"] = plan.MaskTokens.Cast<object?>().ToList(),
                ["placeholder"] = plan.Placeholder,
                ["context_lines"] = plan.ContextLines,
                ["redaction_counts"] = redactionCounts,
                ["binary_evidence"] = (plan.BinaryEvidence ?? []).Select(EvidencePayload).Cast<object?>().ToList(),
                ["safety_limits"] = plan.SafetyLimits?.ToPayload(),
            },
            ["metadata"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["content_type"] = comparison.Metadata.ContentType,
                ["context_lines"] = comparison.Metadata.ContextLines,
                ["baseline_name"] = comparison.Metadata.BaselineName,
                ["comparison_name"] = comparison.Metadata.ComparisonName,
            },
            ["summary"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["before_digest"] = comparison.Summary.BeforeDigest,
                ["after_digest"] = comparison.Summary.AfterDigest,
                ["diff_digest"] = comparison.Summary.DiffDigest,
                ["before_lines"] = comparison.Summary.BeforeLines,
                ["after_lines"] = comparison.Summary.AfterLines,
                ["added_lines"] = comparison.Summary.AddedLines,
                ["removed_lines"] = comparison.Summary.RemovedLines,
                ["changed_lines"] = comparison.Summary.ChangedLines,
            },
        };
    }

    private static OrderedDictionary<string, object?> EvidencePayload(BinarySegmentEvidence evidence) => new(StringComparer.Ordinal)
    {
        ["label"] = evidence.Label,
        ["before_size"] = evidence.BeforeSize,
        ["after_size"] = evidence.AfterSize,
        ["before_digest"] = evidence.BeforeDigest,
        ["after_digest"] = evidence.AfterDigest,
        ["changed"] = evidence.Changed,
        ["reason"] = evidence.Reason,
    };

    /// <summary>The instant as UTC ISO 8601 text: microseconds only when non-zero, then <c>+00:00</c>.</summary>
    internal static string IsoFormat(DateTimeOffset value) => IsoTimestamp.Format(value.ToUniversalTime());
}
