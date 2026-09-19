using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Remote;

/// <summary>The snapshot and manifest payloads and the files they are written to.</summary>
public static partial class CaptureRunner
{
    /// <summary>
    /// <c>capture</c> (id, root, ISO <c>captured_at</c> from <see cref="UtcNow"/>, operator, environment, reason, <c>host</c> from
    /// <see cref="HostName"/>, placeholder, <c>mask_token_count</c>), <c>detections</c>, <c>profile_summary</c> and <c>hunt_hits</c>.
    /// </summary>
    public static OrderedDictionary<string, object?> BuildSnapshotPayload(
        string captureId,
        string root,
        string @operator,
        string environment,
        string reason,
        string placeholder,
        IReadOnlyList<string>? maskTokens,
        IEnumerable<OrderedDictionary<string, object?>> detections,
        IReadOnlyDictionary<string, object?>? profileSummary,
        IEnumerable<OrderedDictionary<string, object?>> huntHits)
    {
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(huntHits);
        var capture = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = captureId,
            ["root"] = root,
            ["captured_at"] = IsoTimestamp.Format(UtcNow()),
            ["operator"] = @operator,
            ["environment"] = environment,
            ["reason"] = reason,
            ["host"] = HostName(),
            ["placeholder"] = placeholder,
            ["mask_token_count"] = maskTokens?.Count ?? 0,
        };
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capture"] = capture,
            ["detections"] = detections.Cast<object?>().ToList(),
            ["profile_summary"] = profileSummary,
            ["hunt_hits"] = huntHits.Cast<object?>().ToList(),
        };
    }

    /// <summary>
    /// <c>schema_version</c>; <c>capture</c> (id, snapshot and manifest file names, captured_at, root, operator, environment, reason,
    /// host); <c>durations</c> in seconds to three places; <c>counts</c> (detections, profile matches, hunt hits, registry scans);
    /// <c>profile_summary</c> totals (0 when absent); <c>redaction</c> (placeholder, mask token count, total redactions); and each
    /// registry scan summary.
    /// </summary>
    /// <exception cref="KeyNotFoundException">A capture field is missing.</exception>
    public static OrderedDictionary<string, object?> BuildManifestPayload(
        IReadOnlyDictionary<string, object?> capture,
        string snapshotPath,
        string manifestPath,
        double detectionDuration,
        double huntDuration,
        double totalDuration,
        long detectionCount,
        long profileMatchCount,
        long huntCount,
        IReadOnlyDictionary<string, object?>? profileSummary,
        string placeholder,
        long maskTokenCount,
        long totalRedactions,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? registryScans = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(snapshotPath);
        ArgumentNullException.ThrowIfNull(manifestPath);
        var summary = profileSummary ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var scans = registryScans ?? [];
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = CaptureManifestSchemaVersion,
            ["capture"] = ManifestCapture(capture, snapshotPath, manifestPath),
            ["durations"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["detection_seconds"] = IniPlugin.EngineRound(detectionDuration, 3),
                ["hunt_seconds"] = IniPlugin.EngineRound(huntDuration, 3),
                ["total_seconds"] = IniPlugin.EngineRound(totalDuration, 3),
            },
            ["counts"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["detections"] = detectionCount,
                ["profile_matches"] = profileMatchCount,
                ["hunt_hits"] = huntCount,
                ["registry_scans"] = (long)scans.Count,
            },
            ["profile_summary"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total_profiles"] = summary.TryGetValue("total_profiles", out var profiles) ? profiles : 0,
                ["total_configs"] = summary.TryGetValue("total_configs", out var configs) ? configs : 0,
            },
            ["redaction"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["placeholder"] = placeholder,
                ["mask_token_count"] = maskTokenCount,
                ["total_redactions"] = totalRedactions,
            },
            ["registry_scans"] = scans.Select(object? (scan) => CopyMapping(scan)).ToList(),
        };
    }

    // Fields are read in a fixed order so the first missing one is the one reported.
    private static OrderedDictionary<string, object?> ManifestCapture(IReadOnlyDictionary<string, object?> capture, string snapshotPath, string manifestPath)
    {
        var block = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        block["id"] = DetectionProfileStore.Subscript(capture, "id");
        block["snapshot_path"] = PathText.Name(snapshotPath);
        block["manifest_path"] = PathText.Name(manifestPath);
        foreach (var key in new[] { "captured_at", "root", "operator", "environment", "reason", "host" })
        {
            block[key] = DetectionProfileStore.Subscript(capture, key);
        }

        return block;
    }

    private static OrderedDictionary<string, object?> BuildSnapshotPayload(
        CaptureIdentity identity,
        string placeholder,
        IReadOnlyList<string> maskTokens,
        IEnumerable<OrderedDictionary<string, object?>> detections,
        IReadOnlyDictionary<string, object?>? profileSummary,
        IEnumerable<OrderedDictionary<string, object?>> huntHits)
        => BuildSnapshotPayload(
            identity.CaptureId,
            identity.Root,
            identity.Operator,
            identity.Environment,
            identity.Reason,
            placeholder,
            maskTokens,
            detections,
            profileSummary,
            huntHits);

    /// <summary>ASCII-escaped JSON, indent 2, sorted keys, platform line breaks, no trailing newline; write failures propagate.</summary>
    internal static void WriteJsonText(string path, OrderedDictionary<string, object?> payload)
    {
        var text = Canonicaliser.DumpsSorted(payload, indent: true, ensureAscii: true);
        WriteText(path, string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal) ? text : text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private static void WriteText(string path, string text) => EngineTextFile.WriteText(path, text);

    internal static string ReadUtf8Text(string path) => EngineTextFile.ReadUtf8Text(path);
}
