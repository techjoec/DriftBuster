using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Models;

/// <summary>A capture's exit code and output, and when it ran, its snapshot and manifest.</summary>
public sealed record CaptureRunResult(int ExitCode, string Output, string Errors, string? SnapshotPath, string? ManifestPath, CaptureManifest? Manifest);
