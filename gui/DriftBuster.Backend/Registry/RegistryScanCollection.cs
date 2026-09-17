namespace DriftBuster.Backend.Registry;

/// <summary>
/// One <see cref="RegistryScanCollector.Collect"/> run: the manifest source summary, and the <c>registry_scan.json</c> path, size and
/// SHA-256 of the file it wrote (null path and hash when the scan was skipped).
/// </summary>
public sealed record RegistryScanCollection(OrderedDictionary<string, object?> Summary, string? ResultPath, long Size, string? Sha256);
