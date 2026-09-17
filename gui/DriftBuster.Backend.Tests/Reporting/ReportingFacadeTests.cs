using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// <see cref="ReportingFacade"/> is <c>driftbuster.reporting</c>'s <c>__all__</c>: every export returns what the helper it re-exports
/// returns for the same inputs.
/// </summary>
public sealed class ReportingFacadeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-reporting-facade-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static DetectionMatch[] Matches() =>
        [new("json", "json", "generic", 0.9, ["synthetic"], new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "SECRET" })];

    [Fact]
    public void DiffAndRedactionExportsForward()
    {
        ReportingFacade.CanonicaliseJson("{\"b\": 1, \"a\": 2}").Should().Be(Canonicaliser.CanonicaliseJson("{\"b\": 1, \"a\": 2}"));
        ReportingFacade.CanonicaliseText("a  \r\nb").Should().Be(Canonicaliser.CanonicaliseText("a  \r\nb"));
        ReportingFacade.CanonicaliseXml("<a><b/></a>").Should().Be(Canonicaliser.CanonicaliseXml("<a><b/></a>"));
        ReportingFacade.RenderUnifiedDiff("a\nb", "a\nc").Should().Be(DiffBuilder.RenderUnifiedDiff("a\nb", "a\nc"));
        var artifact = ReportingFacade.BuildUnifiedDiff("a\nb", "a\nc", label: "cfg");
        artifact.Should().BeEquivalentTo(DiffBuilder.BuildUnifiedDiff("a\nb", "a\nc", label: "cfg"));
        var summary = ReportingFacade.SummariseDiffResult(artifact, versions: ["v1", "v2"]);
        ReportingFacade.SummariseDiffResults([artifact], versions: ["v1", "v2"]).Comparisons.Should().HaveCount(summary.Comparisons.Length);
        ReportingFacade.DiffSummaryToPayload(summary).Keys.Should().Equal(DiffBuilder.DiffSummaryToPayload(summary).Keys);

        var redactor = ReportingFacade.ResolveRedactor(maskTokens: ["SECRET"], placeholder: "#")!;
        redactor.Placeholder.Should().Be("#");
        ReportingFacade.RedactData(new List<object?> { "SECRET" }, redactor).Should().BeEquivalentTo(new List<object?> { "#" });
    }

    [Fact]
    public void ReportExportsForward()
    {
        var snapshotManifest = ReportingFacade.BuildSnapshotManifest(Matches(), outputName: "o", maskTokens: ["SECRET"]);
        snapshotManifest.Keys.Should().Equal(SnapshotManifest.Build(Matches(), outputName: "o", maskTokens: ["SECRET"]).Keys);
        ReportingFacade.IterJsonRecords(Matches()).Should().HaveCount(JsonLinesReport.IterJsonRecords(Matches()).Count());
        ReportingFacade.RenderJsonLines(Matches(), sortKeys: false).Should().Be(JsonLinesReport.RenderJsonLines(Matches(), sortKeys: false));
        ReportingFacade.RenderHtmlReport(Matches(), title: "T").Should().Contain("<title>T</title>");
        ReportingFacade.SummariseDetections(Matches())["total_matches"].Should().Be(1);

        using var lines = new StringWriter();
        ReportingFacade.WriteJsonLines(Matches(), lines);
        lines.ToString().Should().Be(JsonLinesReport.RenderJsonLines(Matches()) + "\n");
        using var page = new StringWriter();
        ReportingFacade.WriteHtmlReport(Matches(), page, legalNotice: "L");
        page.ToString().Should().Contain("<p>L</p>");
        var pagePath = Path.Combine(_tmp.FullName, "page.html");
        ReportingFacade.WriteHtmlReport(Matches(), pagePath, warnings: ["W"]);
        File.ReadAllText(pagePath).Should().Contain("W<br/>Derived data only.");
        var snapshotPath = Path.Combine(_tmp.FullName, "sub", "snapshot.json");
        ReportingFacade.WriteSnapshot(Matches(), snapshotPath, @operator: "op", indent: 1);
        File.ReadAllText(snapshotPath).Should().Contain(" \"operator\": \"op\"");
    }
}
