using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Models;

/// <summary>An export's exit code and output, the manifest (null when nothing was exported) and the snapshot files written.</summary>
public sealed record SqlExportResult(int ExitCode, string Output, string Errors, string? ManifestPath, SqlExportManifest? Manifest, IReadOnlyList<string> SnapshotPaths);
