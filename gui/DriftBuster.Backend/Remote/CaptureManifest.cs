namespace DriftBuster.Backend.Remote;

/// <summary>A capture's manifest (<c>&lt;id&gt;-manifest.json</c>): the snapshot it describes, timings, counts, redaction and registry scans.</summary>
public sealed record CaptureManifest(
    string SchemaVersion,
    CaptureManifestInfo Capture,
    CaptureDurations Durations,
    CaptureCounts Counts,
    CaptureRedaction Redaction,
    IReadOnlyList<RegistryScanSummary> RegistryScans)
{
    public const string CurrentSchemaVersion = "2";
}
