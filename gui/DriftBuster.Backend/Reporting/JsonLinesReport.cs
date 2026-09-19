using System.Text.Json;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// Newline-delimited JSON: <c>{"type":"detection","payload":…}</c> per detection, then <c>{"type":"hunt_hit","payload":…}</c> per hunt
/// hit, each on one line ending in LF. With a redactor every string value in a record is redacted before it is written.
/// </summary>
public static class JsonLinesReport
{
    public static void Write(TextWriter writer, IEnumerable<DetectionPayload> detections, IEnumerable<HuntHitResult> huntHits, RedactionFilter? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(huntHits);
        foreach (var detection in detections)
        {
            WriteRecord(writer, "detection", JsonSerializer.SerializeToNode(detection, ModelJson.TypeInfo<DetectionPayload>()), redactor);
        }

        foreach (var hit in huntHits)
        {
            WriteRecord(writer, "hunt_hit", JsonSerializer.SerializeToNode(hit, ModelJson.TypeInfo<HuntHitResult>()), redactor);
        }
    }

    private static void WriteRecord(TextWriter writer, string type, JsonNode? payload, RedactionFilter? redactor)
    {
        var record = new JsonObject { ["type"] = type, ["payload"] = redactor is null ? payload : redactor.ApplyTo(payload) };
        writer.Write(record.ToJsonString(ModelJson.LineOptions));
        writer.Write('\n');
    }
}
