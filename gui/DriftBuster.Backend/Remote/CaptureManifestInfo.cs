namespace DriftBuster.Backend.Remote;

/// <summary>The capture's identity with the snapshot and manifest file names.</summary>
public sealed record CaptureManifestInfo(string Id, string SnapshotFile, string ManifestFile, DateTimeOffset CapturedAt, string Root, string Operator, string Environment, string Reason, string Host);
