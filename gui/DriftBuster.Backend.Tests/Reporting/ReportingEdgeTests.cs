using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// Reporting adapter edges: an invalid confidence, empty sections, and a redactor given together with mask tokens.
/// </summary>
public sealed class ReportingEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-reporting-edge-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    private static DetectionMatch Match(string token = "value") => new("json", "json", "generic", 0.5, ["r"], Map(("token", token)));

    // A confidence that is not a number leaves the peak at 0.00, and the match block shows the text as given.
    [Fact]
    public void RenderDetectionSummaryToleratesInvalidConfidence()
    {
        IReadOnlyDictionary<string, object?> record = Map(("plugin", "json"), ("format", "json"), ("variant", "generic"), ("confidence", "invalid"), ("reasons", new List<object?>()), ("metadata", Map()));
        IReadOnlyDictionary<string, object?> listConfidence = Map(("format", "json"), ("variant", "generic"), ("confidence", new List<object?> { 1 }));
        IReadOnlyDictionary<string, object?> textConfidence = Map(("format", "json"), ("variant", "generic"), ("confidence", " 0.125 "));

        HtmlReport.RenderDetectionSummary([record]).Should().Contain("<td>json</td><td>generic</td><td>1</td><td>0.00</td>");
        HtmlReport.RenderDetectionSummary([record, listConfidence, textConfidence]).Should().Contain("<td>3</td><td>0.12</td>");
        HtmlReport.RenderMatch(record, 2).Should().Be(
            "<section class=\"match\"><h3>Match 2: json</h3><p><strong>Plugin:</strong> json | <strong>Variant:</strong> generic</p>"
            + "<p><strong>Confidence:</strong> invalid</p><h4>Reasons</h4><ul><li>None provided</li></ul><h4>Metadata</h4><table></table></section>");
    }

    [Fact]
    public void SectionRenderersReturnNothingForEmptyInputs()
    {
        HtmlReport.RenderDiffSection([]).Should().BeEmpty();
        HtmlReport.RenderHuntSection([]).Should().BeEmpty();
        HtmlReport.RenderProfileSummary(Map()).Should().BeEmpty();
        HtmlReport.FormatSafetyNotice(null).Should().BeEmpty();
    }

    [Fact]
    public void RedactorAndMaskTokensTogetherRaise()
    {
        var redactor = new RedactionFilter(["x"]);
        var html = () => HtmlReport.Render([Match()], redactor: redactor, maskTokens: ["y"]);
        html.Should().Throw<EngineValueException>().WithMessage("Provide either an explicit redactor or mask_tokens, not both.");
        var jsonl = () => JsonLinesReport.RenderJsonLines([Match()], redactor: redactor, maskTokens: ["y"]);
        jsonl.Should().Throw<EngineValueException>();
        var snapshot = () => SnapshotManifest.Build([Match()], redactor: redactor, maskTokens: ["y"]);
        snapshot.Should().Throw<EngineValueException>();
    }
}
