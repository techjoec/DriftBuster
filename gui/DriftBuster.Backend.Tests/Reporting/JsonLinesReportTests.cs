using System.Text.Json.Nodes;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>JSON lines: one record per detection then per hunt hit, each on its own line, redacted when asked.</summary>
public sealed class JsonLinesReportTests
{
    [Fact]
    public void Records_come_detections_first_one_per_line_and_redacted()
    {
        var writer = new StringWriter();
        var detection = HtmlReportTests.Detection("/root/a.json", "json", "generic", 0.9, new JsonObject { ["token"] = "SECRET", ["nested"] = new JsonArray("x SECRET") });

        JsonLinesReport.Write(writer, [detection], [HtmlReportTests.Hit("SECRET é")], new RedactionFilter(["SECRET"]));

        var lines = writer.ToString().Split('\n');
        lines.Should().HaveCount(3);
        lines[2].Should().BeEmpty();
        var first = JsonNode.Parse(lines[0])!;
        first["type"].ShouldBeJson("detection");
        first["payload"]!["path"].ShouldBeJson("/root/a.json");
        first["payload"]!["metadata"].ShouldBeJson(new { token = "[REDACTED]", nested = new[] { "x [REDACTED]" } });
        var second = JsonNode.Parse(lines[1])!;
        second["type"].ShouldBeJson("hunt_hit");
        second["payload"]!["excerpt"].ShouldBeJson("[REDACTED] é");
        lines[1].Should().Contain("é", "non-ASCII is written as is");
        detection.Metadata.Text("token").Should().Be("SECRET", "the caller's payload is left alone");
    }

    [Fact]
    public void Without_a_redactor_the_payload_is_written_as_is()
    {
        var writer = new StringWriter();

        JsonLinesReport.Write(writer, [HtmlReportTests.Detection("a", "text", null, 0.5, new JsonObject { ["k"] = "SECRET" })], []);

        JsonNode.Parse(writer.ToString())!["payload"]!["metadata"]!["k"].ShouldBeJson("SECRET");
    }
}
