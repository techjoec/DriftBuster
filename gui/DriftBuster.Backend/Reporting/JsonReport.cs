using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Reporting;

/// <summary><c>driftbuster.reporting.json</c>: the legacy module name, re-exporting the <see cref="JsonLinesReport"/> helpers unchanged.</summary>
public static class JsonReport
{
    /// <inheritdoc cref="JsonLinesReport.IterJsonRecords"/>
    public static IEnumerable<OrderedDictionary<string, object?>> IterJsonRecords(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
        => JsonLinesReport.IterJsonRecords(matches, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata);

    /// <inheritdoc cref="JsonLinesReport.RenderJsonLines"/>
    public static string RenderJsonLines(
        IEnumerable<DetectionMatch> matches,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        bool sortKeys = true)
        => JsonLinesReport.RenderJsonLines(matches, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, sortKeys);

    /// <inheritdoc cref="JsonLinesReport.WriteJsonLines"/>
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
        => JsonLinesReport.WriteJsonLines(matches, stream, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, sortKeys);
}
