using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>Mirror of tests/reporting/test_snapshot.py.</summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-snapshot-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static DetectionMatch Match() => new(
        "json",
        "json",
        "generic",
        0.8,
        ["demo"],
        new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["token"] = "SECRET" });

    private static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    private static OrderedDictionary<string, object?> FirstPayloadMetadata(OrderedDictionary<string, object?> manifest)
        => Map(Map(Map(((List<object?>)manifest["matches"]!)[0])["payload"])["metadata"]);

    [Fact]
    public void BuildSnapshotManifestIncludesRedactionStats()
    {
        var redactor = new RedactionFilter(["SECRET"]);
        var manifest = SnapshotManifest.Build(
            [Match()],
            outputName: "report.json",
            @operator: "analyst",
            redactor: redactor,
            legalMetadata: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["retention_days"] = 10 },
            extraMetadata: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["scan_id"] = "abc-123" });

        manifest["output"].Should().Be("report.json");
        manifest["operator"].Should().Be("analyst");
        Map(manifest["legal"])["retention_days"].Should().Be(10);
        Map(Map(manifest["legal"])["redacted_tokens"]).Should().Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["SECRET"] = 1 });
        FirstPayloadMetadata(manifest)["token"].Should().Be("[REDACTED]");
        var runMetadata = Map(manifest["run_metadata"]);
        runMetadata.Should().Equal(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["scan_id"] = "abc-123",
            ["operator"] = "analyst",
            ["snapshot_output"] = "report.json",
        });
    }

    [Fact]
    public void BuildSnapshotManifestMergesExtraMetadataIntoMatches()
    {
        var manifest = SnapshotManifest.Build(
            [Match()],
            @operator: "investigator",
            outputName: "payload.json",
            extraMetadata: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["scan_id"] = "scan-42", ["source"] = "ci" });

        var metadata = FirstPayloadMetadata(manifest);
        metadata["scan_id"].Should().Be("scan-42");
        metadata["source"].Should().Be("ci");
        metadata["operator"].Should().Be("investigator");
        metadata["snapshot_output"].Should().Be("payload.json");
        Map(manifest["run_metadata"])["operator"].Should().Be("investigator");
    }

    [Fact]
    public void WriteSnapshotCreatesFile()
    {
        var destination = Path.Combine(_tmp.FullName, "snapshot.json");
        SnapshotManifest.Write(
            [Match()],
            destination,
            maskTokens: ["SECRET"],
            outputName: "out",
            extraMetadata: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["scan_id"] = "run-99" });
        File.Exists(destination).Should().BeTrue();
        var contents = File.ReadAllText(destination, System.Text.Encoding.UTF8);
        contents.Should().Contain("out");
        contents.Should().Contain("[REDACTED]");
        contents.Should().Contain("run-99");
    }
}
