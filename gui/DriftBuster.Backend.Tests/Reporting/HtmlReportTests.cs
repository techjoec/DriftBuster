using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary><see cref="HtmlReport"/>.</summary>
public sealed class HtmlReportTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-html-report-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static DetectionMatch Match() => new(
        "xml",
        "xml",
        "resource",
        0.75,
        ["demo"],
        new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "SECRET", ["format"] = "xml" });

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public void RenderHtmlReportIncludesSections()
    {
        var redactor = new RedactionFilter(["SECRET", "token"], placeholder: "***");
        var diff = new DiffArtifact
        {
            CanonicalBefore = "a",
            CanonicalAfter = "b",
            Diff = "-a\n+b",
            Stats = new DiffStats(1, 0, 0),
            ContentType = "text",
            FromLabel = "before",
            ToLabel = "after",
            Label = "config",
        };

        var rule = new HuntRule("rule", string.Empty);
        var hit = new HuntFinding(rule, "/tmp/file.txt", 3, "SECRET value", []);

        var html = HtmlReport.Render(
            [Match()],
            title: "Example",
            diffs: [diff],
            profileSummary: Map(
                ("total_profiles", 1),
                ("profiles", new List<object?> { Map(("name", "default"), ("config_count", 1), ("config_ids", new List<object?> { "cfg1" })) })),
            huntHits: [hit],
            redactor: redactor,
            extraMetadata: Map(("run_id", "XYZ")),
            warnings: ["Check manually"],
            legalNotice: "Handle with care");

        html.Should().Contain("Example");
        html.Should().Contain("Detection Summary");
        html.Should().Contain("***"); // redacted token
        html.Should().NotContain("Run saved"); // ensure we didn't accidentally leak other strings
        html.Should().Contain("Profile Summary");
        html.Should().Contain("Configuration Diffs");
        html.Should().Contain("Hunt Highlights");
        html.Should().Contain("Redaction Summary");
        html.Should().Contain("Handle with care");
    }

    [Fact]
    public void RenderHtmlReportHandlesNoRedactionHits()
    {
        var html = HtmlReport.Render([Match()], warnings: ["Only sample"]);
        html.Should().Contain("Derived data only");
        html.Should().Contain("No configured tokens were encountered");
    }

    [Fact]
    public void WriteHtmlReportAcceptsStreamAndPath()
    {
        using var buffer = new StringWriter();
        HtmlReport.Write([Match()], buffer, title: "Stream Output");
        var contents = buffer.ToString();
        contents.Should().Contain("Stream Output");
        contents.Trim().Should().StartWith("<!doctype html>");

        var target = Path.Combine(_tmp.FullName, "report.html");
        HtmlReport.Write([Match()], target, title: "Disk Output");
        var written = File.ReadAllText(target, System.Text.Encoding.UTF8);
        written.Should().Contain("Disk Output");
        Path.GetFileName(target).Should().NotContain("DriftBuster"); // ensure file naming left to caller
    }

    [Fact]
    public void RenderHtmlReportToleratesMappingInputsAndCorruptEntries()
    {
        var diff = new DiffArtifact
        {
            CanonicalBefore = "old",
            CanonicalAfter = "new",
            Diff = "@@\n-old\n+new",
            Stats = new DiffStats(1, 1, 0),
            ContentType = "text",
            FromLabel = "before",
            ToLabel = "after",
            Label = "Config",
        };
        // NaN is the corrupt confidence a typed match can hold; a text confidence is covered by RenderDetectionSummaryToleratesInvalidConfidence.
        var corrupted = new DetectionMatch("json", "json", "generic", double.NaN, [], new OrderedDictionary<string, object?>(StringComparer.Ordinal));

        var html = HtmlReport.Render(
            matches: [Match(), corrupted],
            diffs: [Map(("label", "Direct"), ("diff", string.Empty)), diff],
            huntHits:
            [
                Map(
                    ("rule", Map(("name", "token"), ("description", "desc"), ("token_name", "secret"))),
                    ("path", "sample"),
                    ("line_number", 1),
                    ("excerpt", "value")),
            ],
            profileSummary: Map(
                ("total_profiles", 1),
                ("profiles", new List<object?> { Map(("name", "demo"), ("config_count", 1), ("config_ids", new object?[] { "cfg" })), "invalid" })));

        html.Should().Contain("Detection Summary");
        html.Should().Contain("Configuration Diffs");
        html.Should().Contain("Hunt Highlights");
        html.Should().Contain("Profile Summary");
    }

    [Fact]
    public void RenderHtmlReportShowsDiffSafetyNotice()
    {
        var html = HtmlReport.Render(
            [Match()],
            diffs:
            [
                Map(
                    ("label", "Large"),
                    ("diff", "-old\n+new"),
                    ("stats", Map(("added_lines", 1))),
                    ("safety_limits", Map(
                        ("diff", Map(("total_lines", 6), ("total_bytes", 120), ("truncated_lines", 2), ("truncated_bytes", 16), ("digest", "sha256:abc"))),
                        ("thresholds", Map(("canonical_bytes", 10), ("diff_bytes", 20), ("diff_lines", 2)))))),
            ]);

        html.Should().Contain("Diff output truncated for safety");
        html.Should().Contain("diff truncated 2 lines");
    }
}
