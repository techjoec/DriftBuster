using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// Newline-delimited JSON records (<c>detection</c>, <c>profile_summary</c>, <c>hunt_hit</c>)
/// with optional token redaction. Hunt hits are <see cref="HuntFinding"/> values or string-keyed mappings.
/// </summary>
public static class JsonLinesReport
{
    /// <summary><c>{"type": kind, "payload": copy}</c>, the whole record redacted when a redactor is given.</summary>
    internal static OrderedDictionary<string, object?> PrepareRecord(string kind, IReadOnlyDictionary<string, object?> payload, RedactionFilter? redactor)
    {
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = kind,
            ["payload"] = ReportValues.Copy(payload),
        };
        return redactor is null ? record : (OrderedDictionary<string, object?>)RedactionFilter.RedactData(record, redactor)!;
    }

    /// <summary>
    /// A mapping is copied; a hit gives its rule (<c>name</c>, <c>description</c>, <c>token_name</c> and, under <c>keywords</c>, its pattern
    /// sources), <c>path</c>, <c>line_number</c> and <c>excerpt</c>.
    /// </summary>
    internal static OrderedDictionary<string, object?> SerialiseHuntHit(object hit)
    {
        if (ReportValues.IsMapping(hit, out var mapping))
        {
            return ReportValues.Copy(mapping);
        }

        var finding = AsFinding(hit);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = finding.Rule.Name,
                ["description"] = finding.Rule.Description,
                ["token_name"] = finding.Rule.TokenName,
                ["keywords"] = finding.Rule.Patterns.Select(pattern => (object?)pattern.ToString()).ToArray(),
            },
            ["path"] = finding.Path,
            ["line_number"] = finding.LineNumber,
            ["excerpt"] = finding.Excerpt,
        };
    }

    internal static HuntFinding AsFinding(object? hit) => hit as HuntFinding
        ?? throw new InvalidDataException($"expected a hunt hit, not '{Infrastructure.EngineBuiltins.TypeName(hit)}'");

    /// <summary>
    /// A detection record per match (metadata updated with <paramref name="extraMetadata"/>), then a <c>profile_summary</c> record when the
    /// summary is non-empty, then a <c>hunt_hit</c> record per hit; the summary and each hit get the run metadata merged under
    /// <c>run_metadata</c> when there is any. A redactor and mask tokens together raise.
    /// </summary>
    public static IEnumerable<OrderedDictionary<string, object?>> IterJsonRecords(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return IterJsonRecordsCore(matches, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata);
    }

    private static IEnumerable<OrderedDictionary<string, object?>> IterJsonRecordsCore(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? profileSummary,
        IEnumerable<object>? huntHits,
        RedactionFilter? redactor,
        IReadOnlyList<string>? maskTokens,
        string placeholder,
        IReadOnlyDictionary<string, object?>? extraMetadata)
    {
        var activeRedactor = RedactionFilter.Resolve(redactor, maskTokens, placeholder);
        var runMetadata = ReportValues.Copy(extraMetadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal));

        foreach (var payload in DetectionPayloads.Iterate(matches, runMetadata))
        {
            yield return PrepareRecord("detection", payload, activeRedactor);
        }

        if (profileSummary is { Count: > 0 })
        {
            var summaryPayload = ReportValues.Copy(profileSummary);
            if (runMetadata.Count > 0)
            {
                ReportValues.MergeRunMetadata(summaryPayload, runMetadata);
            }

            yield return PrepareRecord("profile_summary", summaryPayload, activeRedactor);
        }

        foreach (var rawHit in huntHits ?? [])
        {
            var hitPayload = SerialiseHuntHit(rawHit);
            if (runMetadata.Count > 0)
            {
                ReportValues.MergeRunMetadata(hitPayload, runMetadata);
            }

            yield return PrepareRecord("hunt_hit", hitPayload, activeRedactor);
        }
    }

    /// <summary>Each record as one line of JSON (non-ASCII kept, keys optionally sorted), joined by LF.</summary>
    public static string RenderJsonLines(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        bool sortKeys = true)
    {
        var lines = IterJsonRecords(matches, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata)
            .Select(record => DumpRecord(record, sortKeys))
            .ToList();
        return string.Join('\n', lines);
    }

    /// <summary>Each record written to <paramref name="stream"/> as it is produced, followed by LF.</summary>
    public static void WriteJsonLines(
        IEnumerable<DetectionMatch> matches,
        TextWriter stream,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        bool sortKeys = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var record in IterJsonRecords(matches, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata))
        {
            stream.Write(DumpRecord(record, sortKeys));
            stream.Write('\n');
        }
    }

    private static string DumpRecord(OrderedDictionary<string, object?> record, bool sortKeys)
        => Canonicaliser.Dumps(ReportValues.ToJsonValue(record), indent: false, ensureAscii: false, sortKeys);
}
