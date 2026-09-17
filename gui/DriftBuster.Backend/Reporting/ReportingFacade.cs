using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// The diff, redaction, JSON lines, HTML, snapshot and summary helpers under one name.
/// <c>DiffResult</c> is <see cref="DiffArtifact"/>, <c>DiffResultSummary</c> is <see cref="DiffResultSummary"/> and
/// <c>RedactionFilter</c> is <see cref="Diff.RedactionFilter"/>.
/// </summary>
public static class ReportingFacade
{
    /// <inheritdoc cref="SnapshotManifest.Build"/>
    public static OrderedDictionary<string, object?> BuildSnapshotManifest(
        IEnumerable<DetectionMatch> matches,
        string? outputName = null,
        string? @operator = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? legalMetadata = null,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
        => SnapshotManifest.Build(matches, outputName, @operator, redactor, maskTokens, placeholder, legalMetadata, extraMetadata);

    /// <inheritdoc cref="DiffBuilder.BuildUnifiedDiff"/>
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
        => DiffBuilder.BuildUnifiedDiff(before, after, contentType, fromLabel, toLabel, label, redactor, maskTokens, placeholder, contextLines);

    /// <inheritdoc cref="Canonicaliser.CanonicaliseJson"/>
    public static string CanonicaliseJson(string payload) => Canonicaliser.CanonicaliseJson(payload);

    /// <inheritdoc cref="Canonicaliser.CanonicaliseText"/>
    public static string CanonicaliseText(string payload) => Canonicaliser.CanonicaliseText(payload);

    /// <inheritdoc cref="Canonicaliser.CanonicaliseXml"/>
    public static string CanonicaliseXml(string payload) => Canonicaliser.CanonicaliseXml(payload);

    /// <inheritdoc cref="DiffBuilder.DiffSummaryToPayload"/>
    public static OrderedDictionary<string, object?> DiffSummaryToPayload(DiffResultSummary summary) => DiffBuilder.DiffSummaryToPayload(summary);

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

    /// <inheritdoc cref="RedactionFilter.RedactData"/>
    public static object? RedactData(object? data, RedactionFilter redactor) => RedactionFilter.RedactData(data, redactor);

    /// <inheritdoc cref="HtmlReport.Render"/>
    public static string RenderHtmlReport(
        IEnumerable<DetectionMatch> matches,
        string title = "DriftBuster Report",
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IReadOnlyList<string>? warnings = null,
        string? legalNotice = null)
        => HtmlReport.Render(matches, title, diffs, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, warnings, legalNotice);

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

    /// <inheritdoc cref="DiffBuilder.RenderUnifiedDiff"/>
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
        => DiffBuilder.RenderUnifiedDiff(before, after, contentType, fromLabel, toLabel, redactor, maskTokens, placeholder, contextLines);

    /// <inheritdoc cref="RedactionFilter.Resolve"/>
    public static RedactionFilter? ResolveRedactor(RedactionFilter? redactor = null, IReadOnlyList<string>? maskTokens = null, string placeholder = RedactionFilter.DefaultPlaceholder)
        => RedactionFilter.Resolve(redactor, maskTokens, placeholder);

    /// <inheritdoc cref="DetectionSummary.Summarise"/>
    public static OrderedDictionary<string, object?> SummariseDetections(IEnumerable<DetectionMatch> matches) => DetectionSummary.Summarise(matches);

    /// <inheritdoc cref="DiffBuilder.SummariseDiffResult"/>
    public static DiffResultSummary SummariseDiffResult(
        DiffArtifact result,
        IReadOnlyList<string>? versions = null,
        string? baselineName = null,
        string? comparisonName = null)
        => DiffBuilder.SummariseDiffResult(result, versions, baselineName, comparisonName);

    /// <inheritdoc cref="DiffBuilder.SummariseDiffResults"/>
    public static DiffResultSummary SummariseDiffResults(
        IReadOnlyList<DiffArtifact> results,
        IReadOnlyList<string>? versions = null,
        IReadOnlyList<string?>? baselineNames = null,
        IReadOnlyList<string?>? comparisonNames = null)
        => DiffBuilder.SummariseDiffResults(results, versions, baselineNames, comparisonNames);

    /// <inheritdoc cref="HtmlReport.Write(IEnumerable{DetectionMatch}, string, string, IEnumerable{object}?, IReadOnlyDictionary{string, object?}?, IEnumerable{object}?, RedactionFilter?, IReadOnlyList{string}?, string, IReadOnlyDictionary{string, object?}?, IReadOnlyList{string}?, string?)"/>
    public static void WriteHtmlReport(
        IEnumerable<DetectionMatch> matches,
        string destination,
        string title = "DriftBuster Report",
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IReadOnlyList<string>? warnings = null,
        string? legalNotice = null)
        => HtmlReport.Write(matches, destination, title, diffs, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, warnings, legalNotice);

    /// <inheritdoc cref="HtmlReport.Write(IEnumerable{DetectionMatch}, TextWriter, string, IEnumerable{object}?, IReadOnlyDictionary{string, object?}?, IEnumerable{object}?, RedactionFilter?, IReadOnlyList{string}?, string, IReadOnlyDictionary{string, object?}?, IReadOnlyList{string}?, string?)"/>
    public static void WriteHtmlReport(
        IEnumerable<DetectionMatch> matches,
        TextWriter destination,
        string title = "DriftBuster Report",
        IEnumerable<object>? diffs = null,
        IReadOnlyDictionary<string, object?>? profileSummary = null,
        IEnumerable<object>? huntHits = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        IReadOnlyList<string>? warnings = null,
        string? legalNotice = null)
        => HtmlReport.Write(matches, destination, title, diffs, profileSummary, huntHits, redactor, maskTokens, placeholder, extraMetadata, warnings, legalNotice);

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

    /// <inheritdoc cref="SnapshotManifest.Write"/>
    public static void WriteSnapshot(
        IEnumerable<DetectionMatch> matches,
        string destination,
        string? @operator = null,
        string? outputName = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? legalMetadata = null,
        int indent = 2,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
        => SnapshotManifest.Write(matches, destination, @operator, outputName, redactor, maskTokens, placeholder, legalMetadata, indent, extraMetadata);
}
