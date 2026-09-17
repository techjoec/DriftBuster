namespace DriftBuster.Backend.Sql;

/// <summary>What <see cref="Sqlite3Cursor.FetchAll"/> returns: <c>cursor.description</c> names and the fetched rows, in step order.</summary>
internal sealed record Sqlite3Rows(IReadOnlyList<string> Description, IReadOnlyList<object?[]> Rows);
