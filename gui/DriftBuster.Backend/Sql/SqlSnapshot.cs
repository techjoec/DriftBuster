namespace DriftBuster.Backend.Sql;

/// <summary>A whole database export.</summary>
/// <param name="Database">The database file's name.</param>
/// <param name="Dialect">Always <c>sqlite</c>.</param>
/// <param name="CapturedAt">The UTC capture time (<see cref="IsoTimestamp.Format"/>).</param>
/// <param name="Path">The path as given, lexically normalised.</param>
/// <param name="Tables">The exported tables in name order.</param>
public sealed record SqlSnapshot(string Database, string Dialect, string CapturedAt, string Path, IReadOnlyList<SnapshotTable> Tables)
{
    /// <summary>database, dialect, captured_at, path, tables.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["database"] = Database,
        ["dialect"] = Dialect,
        ["captured_at"] = CapturedAt,
        ["path"] = Path,
        ["tables"] = Tables.Select(object? (table) => table.ToDict()).ToList(),
    };
}
