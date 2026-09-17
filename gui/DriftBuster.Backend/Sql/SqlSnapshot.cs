namespace DriftBuster.Backend.Sql;

/// <summary><c>driftbuster.sql.snapshots.SqlSnapshot</c>: a whole database export.</summary>
/// <param name="Database">The database file's name (<c>Path.name</c>).</param>
/// <param name="Dialect">Always <c>sqlite</c>.</param>
/// <param name="CapturedAt">The UTC capture time, <c>datetime.isoformat()</c>.</param>
/// <param name="Path">The path as given, normalised as <c>str(Path(path))</c>.</param>
/// <param name="Tables">The exported tables in name order.</param>
public sealed record SqlSnapshot(string Database, string Dialect, string CapturedAt, string Path, IReadOnlyList<SnapshotTable> Tables)
{
    /// <summary><c>to_dict()</c>: database, dialect, captured_at, path, tables.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["database"] = Database,
        ["dialect"] = Dialect,
        ["captured_at"] = CapturedAt,
        ["path"] = Path,
        ["tables"] = Tables.Select(object? (table) => table.ToDict()).ToList(),
    };
}
