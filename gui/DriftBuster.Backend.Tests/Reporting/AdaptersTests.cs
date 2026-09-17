using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>Mirror of tests/reporting/test_adapters.py.</summary>
public sealed class AdaptersTests
{
    private static DetectionMatch Match() => new(
        "demo",
        "json",
        "default",
        0.5,
        ["synthetic"],
        new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "value" });

    private static OrderedDictionary<string, object?> RunId() => new(StringComparer.Ordinal) { ["run_id"] = "abc" };

    [Fact]
    public void IterDetectionPayloadsMergesExtraMetadata()
    {
        var match = Match();
        var payloads = DetectionPayloads.Iterate([match], extraMetadata: RunId()).ToList();
        payloads.Should().HaveCount(1);
        var metadata = (OrderedDictionary<string, object?>)payloads[0]["metadata"]!;
        metadata["token"].Should().Be("value");
        metadata["run_id"].Should().Be("abc");
        match.Metadata.Should().BeEquivalentTo(new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "value" });
    }

    [Fact]
    public void JsonRecordsReuseDetectionPayloads()
    {
        var match = Match();
        var expected = DetectionPayloads.Iterate([match], extraMetadata: RunId()).First();
        var record = JsonLinesReport.IterJsonRecords([match], extraMetadata: RunId()).First();
        record["type"].Should().Be("detection");
        PythonValues.Equal(record["payload"], expected).Should().BeTrue();
    }

    [Fact]
    public void RenderHtmlReportIncludesSharedMetadata()
    {
        var html = HtmlReport.Render([Match()], extraMetadata: RunId());
        html.Should().Contain("run_id");
        html.Should().Contain("abc");
    }
}
