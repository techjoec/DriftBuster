namespace DriftBuster.Backend.Remote;

/// <summary>What <see cref="CaptureRunner.RunCapture"/> did.</summary>
/// <param name="ExitCode">0 when both files were written, 1 when a check refused the run.</param>
/// <param name="SnapshotPath">The snapshot file; null when refused.</param>
/// <param name="ManifestPath">The manifest file; null when refused.</param>
/// <param name="Snapshot">The redacted snapshot payload.</param>
/// <param name="Manifest">The manifest payload.</param>
public sealed record CaptureRunOutcome(
    int ExitCode,
    string? SnapshotPath = null,
    string? ManifestPath = null,
    OrderedDictionary<string, object?>? Snapshot = null,
    OrderedDictionary<string, object?>? Manifest = null);
