using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>Mirror of tests/reporting/test_json_adapter.py.</summary>
public sealed class JsonAdapterTests
{
    private static DetectionMatch Match(string formatName, OrderedDictionary<string, object?>? metadata = null)
        => new("json", formatName, "generic", 0.9, ["synthetic"], metadata);

    private static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    [Fact]
    public void IterJsonRecordsEnrichesMetadataAndAppliesRedaction()
    {
        var redactor = new RedactionFilter(["SECRET"]);
        var matches = new[] { Match("json", new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "SECRET" }) };

        var rule = new HuntRule("token", string.Empty);
        var hit = new HuntFinding(rule, "/tmp/secret.txt", 1, "SECRET", []);

        var records = JsonLinesReport.IterJsonRecords(
            matches,
            profileSummary: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["total"] = 1 },
            huntHits: [hit],
            redactor: redactor,
            extraMetadata: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["run_id"] = "abc" }).ToList();

        var detection = records[0];
        detection["type"].Should().Be("detection");
        var payload = Map(detection["payload"]);
        Map(payload["metadata"])["token"].Should().Be("[REDACTED]");
        Map(payload["metadata"])["run_id"].Should().Be("abc");

        var summary = records[1];
        summary["type"].Should().Be("profile_summary");
        Map(Map(summary["payload"])["run_metadata"])["run_id"].Should().Be("abc");

        var hunt = records[2];
        Map(hunt["payload"])["excerpt"].Should().Be("[REDACTED]");
        Map(Map(hunt["payload"])["run_metadata"])["run_id"].Should().Be("abc");
    }

    [Fact]
    public void IterJsonRecordsAcceptsMappingHuntHits()
    {
        var records = JsonLinesReport.IterJsonRecords(
            [Match("json")],
            huntHits:
            [
                new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["rule"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "mapping" },
                    ["path"] = "file",
                    ["line_number"] = 2,
                    ["excerpt"] = "value",
                },
            ]).ToList();
        var hunt = records.Where(record => string.Equals((string)record["type"]!, "hunt_hit", StringComparison.Ordinal)).ToList();
        hunt.Should().HaveCount(1);
        Map(Map(hunt[0]["payload"])["rule"])["name"].Should().Be("mapping");
        Map(hunt[0]["payload"])["line_number"].Should().Be(2);
    }

    // `legacy_json.iter_json_records is iter_json_records`: the C# re-export forwards, so the check is that each forwarder returns what
    // the helper it re-exports returns for the same inputs.
    [Fact]
    public void LegacyModuleReexportsNewHelpers()
    {
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "value" };
        JsonReport.RenderJsonLines([Match("json", metadata)]).Should().Be(JsonLinesReport.RenderJsonLines([Match("json", metadata)]));
        JsonReport.IterJsonRecords([Match("json", metadata)]).Select(record => Canonicaliser.DumpsSorted(ReportValues.ToJsonValue(record), indent: false))
            .Should().Equal(JsonLinesReport.IterJsonRecords([Match("json", metadata)]).Select(record => Canonicaliser.DumpsSorted(ReportValues.ToJsonValue(record), indent: false)));
        using var legacy = new StringWriter();
        using var current = new StringWriter();
        JsonReport.WriteJsonLines([Match("json", metadata)], legacy);
        JsonLinesReport.WriteJsonLines([Match("json", metadata)], current);
        legacy.ToString().Should().Be(current.ToString());
    }

    [Fact]
    public void RenderAndWriteJsonLinesPreserveOrdering()
    {
        var matches = new[] { Match("json", new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "value" }) };

        var text = JsonLinesReport.RenderJsonLines(matches, sortKeys: true);
        (!text.Contains('\n', StringComparison.Ordinal) || !text.EndsWith('\n')).Should().BeTrue();

        using var stream = new StringWriter();
        JsonLinesReport.WriteJsonLines(matches, stream, sortKeys: false);
        var contents = stream.ToString();
        contents.Should().EndWith("\n");
        contents.Should().Contain("token");
    }
}
