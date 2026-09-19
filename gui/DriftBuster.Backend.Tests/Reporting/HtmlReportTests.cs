using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>The HTML page: sections, escaping, redaction and the empty page.</summary>
public sealed class HtmlReportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    internal static DetectionPayload Detection(string path, string format, string? variant, double confidence, JsonObject? metadata = null)
        => new(path, "plugin", format, variant, confidence, ["found <it>"], metadata ?? []);

    internal static HuntHitResult Hit(string excerpt, string? token = "server_name")
        => new(new HuntRuleResult("server-name", "Server names", token, ["server"], ["pattern"]), "/root/app.config", "app.config", 3, excerpt);

    [Fact]
    public void A_page_has_the_summary_every_detection_the_hunt_hits_and_a_redaction_summary()
    {
        var redactor = new RedactionFilter(["SECRET"], placeholder: "***");
        var detections = new[]
        {
            Detection("/root/a.json", "json", "generic", 0.5, new JsonObject { ["token"] = "SECRET", ["count"] = 2 }),
            Detection("/root/b.json", "json", "generic", 0.915),
            Detection("/root/c.xml", "xml", null, 0.7),
        };

        var html = HtmlReport.Render("Drift <report>", detections, [Hit("host SECRET here")], redactor, Generated);

        html.Should().StartWith("<!doctype html>").And.EndWith("</body></html>");
        html.Should().Contain("<title>Drift &lt;report&gt;</title>").And.Contain("Generated at 2026-01-02T03:04:05Z");
        html.Should().Contain("<tr><td>json</td><td>generic</td><td>2</td><td>0.92</td></tr>");
        html.Should().Contain("<tr><td>xml</td><td>—</td><td>1</td><td>0.70</td></tr>");
        html.Should().Contain("<h3>Match 1: json</h3>").And.Contain("<strong>File:</strong> /root/a.json");
        html.Should().Contain("<li>found &lt;it&gt;</li>");
        html.Should().Contain("<tr><th>count</th><td>2</td></tr><tr><th>token</th><td>***</td></tr>");
        html.Should().Contain("<strong>app.config</strong> — line 3").And.Contain("<code>host *** here</code>").And.Contain("token: server_name");
        html.Should().Contain("Redaction active").And.Contain("<li>SECRET → *** (occurrences: 2)</li>");
        html.Should().NotContain("SECRET value").And.NotContain(">SECRET<");
    }

    [Fact]
    public void A_page_without_detections_hits_or_tokens_says_so()
    {
        var html = HtmlReport.Render(HtmlReport.DefaultTitle, [], [], redactor: null, Generated);

        html.Should().NotContain("Detection Summary").And.NotContain("Hunt Highlights").And.NotContain("Redaction active");
        html.Should().Contain("No configured tokens were encountered in this report.");
    }
}
