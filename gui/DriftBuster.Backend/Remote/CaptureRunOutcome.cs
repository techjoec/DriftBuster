namespace DriftBuster.Backend.Remote;

/// <summary>What <see cref="CaptureRunner.RunCapture"/> did.</summary>
/// <param name="ExitCode">The command's status: 0 when both files were written, 1 when a check refused the run.</param>
/// <param name="SnapshotPath">The snapshot file written, as <c>str(path)</c>; null when the run was refused.</param>
/// <param name="ManifestPath">The manifest file written; null when the run was refused.</param>
/// <param name="Snapshot">The redacted snapshot payload written to <paramref name="SnapshotPath"/>.</param>
/// <param name="Manifest">The manifest payload written to <paramref name="ManifestPath"/>.</param>
public sealed record CaptureRunOutcome(
    int ExitCode,
    string? SnapshotPath = null,
    string? ManifestPath = null,
    OrderedDictionary<string, object?>? Snapshot = null,
    OrderedDictionary<string, object?>? Manifest = null);
