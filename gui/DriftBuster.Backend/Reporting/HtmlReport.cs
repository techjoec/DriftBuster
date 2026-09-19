using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// A static HTML page: the generation time, the handling notice, a summary table of formats and variants, one section per detection,
/// the hunt hits and the redaction summary. Every text shown goes through the redactor (when there is one) and is HTML-encoded.
/// </summary>
public static partial class HtmlReport
{
    public const string DefaultTitle = "DriftBuster Report";

    private const string NoValue = "—";

    public static string Render(
        string title,
        IReadOnlyList<DetectionPayload> detections,
        IReadOnlyList<HuntHitResult> huntHits,
        RedactionFilter? redactor,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(huntHits);
        var text = new Text(redactor);
        var page = new StringBuilder()
            .Append(HeaderBeforeTitle).Append(WebUtility.HtmlEncode(title)).Append(HeaderAfterTitle)
            .Append("<div class=\"meta\">Generated at ")
            .Append(generatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</div>\n")
            .Append(Warnings(redactor)).Append('\n');
        if (detections.Count > 0)
        {
            page.Append(Summary(detections, text)).Append('\n');
        }

        for (var index = 0; index < detections.Count; index++)
        {
            page.Append(Detection(detections[index], index + 1, text)).Append('\n');
        }

        if (huntHits.Count > 0)
        {
            page.Append(Hunt(huntHits, text)).Append('\n');
        }

        return page.Append(RedactionSummary(redactor)).Append("\n</body></html>").ToString();
    }

    // Redacts, then HTML-encodes.
    private sealed class Text(RedactionFilter? redactor)
    {
        public string this[string? value] => WebUtility.HtmlEncode(value is null ? string.Empty : redactor?.Apply(value) ?? value);
    }

    private static string Warnings(RedactionFilter? redactor)
    {
        var messages = new List<string> { "Derived data only. Do not redistribute without legal approval." };
        if (redactor is not null)
        {
            messages.Add($"Tokens replaced with {WebUtility.HtmlEncode(redactor.Placeholder)}. Original values are not stored in this report.");
            messages.Add("<span class=\"badge\">Redaction active</span>");
        }

        return "<div class=\"warning\">" + string.Join("<br/>", messages) + "</div>";
    }

    // One row per format and variant, ordinal order, with the match count and the peak confidence.
    private static string Summary(IReadOnlyList<DetectionPayload> detections, Text text)
    {
        var rows = detections
            .GroupBy(detection => (detection.Format, Variant: detection.Variant ?? NoValue))
            .OrderBy(group => group.Key.Format, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Variant, StringComparer.Ordinal)
            .Select(group => string.Create(
                CultureInfo.InvariantCulture,
                $"<tr><td>{text[group.Key.Format]}</td><td>{text[group.Key.Variant]}</td><td>{group.Count()}</td><td>{group.Max(detection => detection.Confidence):0.00}</td></tr>"));
        return "<section class=\"match summary\"><h2>Detection Summary</h2><table class=\"summary-table\">"
            + "<thead><tr><th>Format</th><th>Variant</th><th>Matches</th><th>Peak confidence</th></tr></thead>"
            + "<tbody>" + string.Concat(rows) + "</tbody></table></section>";
    }

    private static string Detection(DetectionPayload detection, int index, Text text)
    {
        var reasons = detection.Reasons.Count > 0 ? string.Concat(detection.Reasons.Select(reason => $"<li>{text[reason]}</li>")) : "<li>None provided</li>";
        var metadata = string.Concat(detection.Metadata
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"<tr><th>{text[pair.Key]}</th><td>{text[ValueText(pair.Value)]}</td></tr>"));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<section class=\"match\"><h3>Match {index}: {text[detection.Format]}</h3>"
            + $"<p><strong>File:</strong> {text[detection.Path]}</p>"
            + $"<p><strong>Plugin:</strong> {text[detection.Plugin]} | <strong>Variant:</strong> {(detection.Variant is null ? NoValue : text[detection.Variant])}</p>"
            + $"<p><strong>Confidence:</strong> {detection.Confidence:0.00}</p>"
            + $"<h4>Reasons</h4><ul>{reasons}</ul><h4>Metadata</h4><table>{metadata}</table></section>");
    }

    // A string as it is; anything else as compact JSON.
    private static string ValueText(JsonNode? value)
        => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : value?.ToJsonString(ModelJson.LineOptions) ?? "null";

    private static string Hunt(IReadOnlyList<HuntHitResult> hits, Text text)
    {
        var items = hits.Select(hit =>
        {
            var badge = hit.Rule.TokenName is { Length: > 0 } token ? $" <span class=\"badge\">token: {text[token]}</span>" : string.Empty;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"<li><strong>{text[hit.RelativePath ?? hit.Path]}</strong> — line {hit.LineNumber}<br/><em>{text[hit.Rule.Description]}</em>{badge}<br/><code>{text[hit.Excerpt]}</code></li>");
        });
        return "<section class=\"hunt-section\"><h2>Hunt Highlights</h2><ul>" + string.Concat(items) + "</ul></section>";
    }

    private static string RedactionSummary(RedactionFilter? redactor)
    {
        var body = redactor is { HasHits: true }
            ? "<ul>" + string.Concat(redactor.Stats()
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"<li>{WebUtility.HtmlEncode(pair.Key)} → {WebUtility.HtmlEncode(redactor.Placeholder)} (occurrences: {pair.Value})</li>"))) + "</ul>"
            : "<p>No configured tokens were encountered in this report. Manually inspect before external sharing.</p>";
        return "<div class=\"redaction-summary\"><h2>Redaction Summary</h2>" + body + "</div>";
    }
}
