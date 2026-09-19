namespace DriftBuster.Backend.Sql;

/// <summary>A whole database export (<c>*-sql-snapshot.json</c>).</summary>
/// <param name="Database">The database file's name.</param>
/// <param name="Dialect">Always <c>sqlite</c>.</param>
/// <param name="CapturedAt">When the export ran (UTC).</param>
/// <param name="Path">The database path as given.</param>
/// <param name="Tables">The exported tables in name order.</param>
public sealed record SqlSnapshot(string Database, string Dialect, DateTimeOffset CapturedAt, string Path, IReadOnlyList<SqlSnapshotTable> Tables);
