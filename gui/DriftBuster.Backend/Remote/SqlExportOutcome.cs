namespace DriftBuster.Backend.Remote;

/// <summary>What <see cref="CaptureRunner.RunSqlExport"/> did.</summary>
/// <param name="ExitCode">0 when every database was exported, 1 when any was missing or failed.</param>
/// <param name="ManifestPath">The <c>sql-manifest.json</c> written after every database was tried.</param>
/// <param name="Manifest">The manifest payload: <c>captured_at</c>, <c>exports</c> and <c>options</c>.</param>
/// <param name="SnapshotPaths">Each snapshot file written, in database order.</param>
public sealed record SqlExportOutcome(int ExitCode, string ManifestPath, OrderedDictionary<string, object?> Manifest, IReadOnlyList<string> SnapshotPaths);
