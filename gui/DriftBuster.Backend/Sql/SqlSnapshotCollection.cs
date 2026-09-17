namespace DriftBuster.Backend.Sql;

/// <summary>
/// One <see cref="SqlSnapshotCollector.Collect"/> run: the manifest source summary, the manifest's <c>sql_exports</c> metadata entry
/// (null when the source was skipped), and the <c>sql-snapshot.json</c> path, size and SHA-256 of the file it wrote (null path and hash
/// when skipped).
/// </summary>
public sealed record SqlSnapshotCollection(
    OrderedDictionary<string, object?> Summary,
    OrderedDictionary<string, object?>? Metadata,
    string? ResultPath,
    long Size,
    string? Sha256);
