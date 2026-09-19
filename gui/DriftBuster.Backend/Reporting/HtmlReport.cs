using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// A static HTML report with embedded redaction warnings.
/// Diffs are <see cref="DiffArtifact"/> values or string-keyed mappings; hunt hits are <see cref="Hunt.HuntFinding"/> values or mappings.
/// </summary>
public static partial class HtmlReport
{
    private const string DefaultTitle = "DriftBuster Report";

    /// <summary>The clock for the "Generated at" line (test seam).</summary>
    internal static Func<DateTimeOffset> UtcNow { get; set; } = IsoTimestamp.UtcNow;

    /// <summary>
    /// Every payload (detections with the extra metadata, diffs, hunt hits, and the profile summary with the extra metadata under
    /// <c>run_metadata</c>) is redacted before anything renders; then the header, generation time, warnings, detection summary table, one
    /// section per match, profile summary, diffs, hunt highlights and redaction summary are joined by LF. A redactor and mask tokens
    /// together raise. <paramref name="warnings"/> is enumerated after every payload is prepared.
    /// </summary>
    public static string Render(
        IEnumerable<DetectionMatch> matches,
        string title = DefaultTitle,
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IEnumerable<string>? warnings = null,
        string? legalNotice = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(title);
        var activeRedactor = RedactionFilter.Resolve(redactor, maskTokens, placeholder);
        var preparedMatches = DetectionPayloads.Iterate(matches, extraMetadata)
            .Select(record => Redact(record, activeRedactor))
            .ToList();
        var preparedDiffs = (diffs ?? []).Select(diff => Redact(SerialiseDiff(diff), activeRedactor)).ToList();
        var preparedHunts = PrepareHunts(huntHits, extraMetadata, activeRedactor);
        OrderedDictionary<string, object?>? preparedSummary = null;
        if (profileSummary is { Count: > 0 })
        {
            var summaryPayload = ReportValues.Copy(profileSummary);
            if (extraMetadata is { Count: > 0 })
            {
                ReportValues.MergeRunMetadata(summaryPayload, extraMetadata);
            }

            preparedSummary = Redact(summaryPayload, activeRedactor);
        }

        var generatedAt = IsoTimestamp.Format(UtcNow()).Replace("+00:00", "Z", StringComparison.Ordinal);
        var parts = new List<string>
        {
            HeaderBeforeTitle + ReportValues.Escape(title) + HeaderAfterTitle,
            $"<div class=\"meta\">Generated at {ReportValues.Escape(generatedAt)}</div>",
            RenderWarnings(warnings, activeRedactor),
        };
        AppendSections(parts, preparedMatches, preparedSummary, preparedDiffs, preparedHunts);
        parts.Add(RenderRedactionSummary(activeRedactor, legalNotice));
        parts.Add("</body></html>");
        return string.Join('\n', parts);
    }

    /// <summary>The rendered report written to a stream as is.</summary>
    public static void Write(
        IEnumerable<DetectionMatch> matches,
        TextWriter destination,
        string title = DefaultTitle,
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IEnumerable<string>? warnings = null,
        string? legalNotice = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Render(matches, title, diffs, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, warnings, legalNotice));
    }

    /// <summary>The rendered report written to a path as UTF-8 with the platform's line breaks; a write failure raises the runtime's exception.</summary>
    public static void Write(
        IEnumerable<DetectionMatch> matches,
        string destination,
        string title = DefaultTitle,
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IEnumerable<string>? warnings = null,
        string? legalNotice = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var html = Render(matches, title, diffs, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, warnings, legalNotice);
        EngineTextFile.WriteText(destination, ReportValues.TextModeNewLines(html));
    }

    private static OrderedDictionary<string, object?> Redact(OrderedDictionary<string, object?> payload, RedactionFilter? redactor)
        => redactor is null ? payload : (OrderedDictionary<string, object?>)RedactionFilter.RedactData(payload, redactor)!;

    private static List<OrderedDictionary<string, object?>> PrepareHunts(
        IEnumerable<object>? huntHits,
        IReadOnlyDictionary<string, object?>? extraMetadata,
        RedactionFilter? redactor)
    {
        var prepared = new List<OrderedDictionary<string, object?>>();
        foreach (var hit in huntHits ?? [])
        {
            var entry = SerialiseHuntHit(hit);
            if (extraMetadata is { Count: > 0 })
            {
                ReportValues.MergeRunMetadata(entry, extraMetadata);
            }

            prepared.Add(Redact(entry, redactor));
        }

        return prepared;
    }

    // The warning block: the caller's warnings, the derived-data notice and, with a redactor, the placeholder notice (whose placeholder
    // is escaped twice: once inside the message and again with the message) and the raw "Redaction active" badge. A message
    // starting "<span" is kept raw.
    private static string RenderWarnings(IEnumerable<string>? warnings, RedactionFilter? redactor)
    {
        var messages = new List<string>(warnings ?? []) { "Derived data only. Do not redistribute without legal approval." };
        if (redactor is not null)
        {
            messages.Add($"Tokens replaced with {ReportValues.Escape(redactor.Placeholder)}. Original values are not stored in this report.");
            messages.Add("<span class=\"badge\">Redaction active</span>");
        }

        var formatted = messages.Select(message => message.StartsWith("<span", StringComparison.Ordinal) ? message : ReportValues.Escape(message));
        return "<div class=\"warning\">" + string.Join("<br/>", formatted) + "</div>";
    }

    private static void AppendSections(
        List<string> parts,
        List<OrderedDictionary<string, object?>> matches,
        OrderedDictionary<string, object?>? profileSummary,
        List<OrderedDictionary<string, object?>> diffs,
        List<OrderedDictionary<string, object?>> hunts)
    {
        var summaryBlock = RenderDetectionSummary(matches);
        if (summaryBlock.Length > 0)
        {
            parts.Add(summaryBlock);
        }

        for (var index = 0; index < matches.Count; index++)
        {
            parts.Add(RenderMatch(matches[index], index + 1));
        }

        if (profileSummary is { Count: > 0 })
        {
            parts.Add(RenderProfileSummary(profileSummary));
        }

        if (diffs.Count > 0)
        {
            parts.Add(RenderDiffSection(diffs));
        }

        if (hunts.Count > 0)
        {
            parts.Add(RenderHuntSection(hunts));
        }
    }

    private static string RenderRedactionSummary(RedactionFilter? redactor, string? legalNotice)
    {
        var lines = new List<string> { "<div class=\"redaction-summary\">", "<h2>Redaction Summary</h2>" };
        if (redactor is { HasHits: true })
        {
            lines.Add("<ul>");
            var stats = redactor.Stats();
            var tokens = stats.Keys.Order(Comparer<string>.Create(PathText.CompareCodePoints)).ToList();
            foreach (var token in tokens)
            {
                lines.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<li>{ReportValues.Escape(token)} \u2192 {ReportValues.Escape(redactor.Placeholder)} (occurrences: {stats[token]})</li>"));
            }

            lines.Add("</ul>");
        }
        else
        {
            lines.Add("<p>No configured tokens were encountered in this report. Manually inspect before external sharing.</p>");
        }

        if (!string.IsNullOrEmpty(legalNotice))
        {
            lines.Add($"<p>{ReportValues.Escape(legalNotice)}</p>");
        }

        return string.Join('\n', lines) + "</div>";
    }
}
