using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Sql;

/// <summary>One exported table.</summary>
/// <param name="Name">The table's name.</param>
/// <param name="Schema">Its <c>CREATE TABLE</c> text, or null when SQLite holds none.</param>
/// <param name="Columns">The column names in order.</param>
/// <param name="RowCount">Every row in the table, however many were exported.</param>
/// <param name="Rows">
/// Each exported row by column: a masked column holds the placeholder, a hashed column <c>sha256:&lt;hex&gt;</c>, any other the value
/// by its SQLite storage class (a BLOB as <c>{"type": "base64", "value": ...}</c>).
/// </param>
/// <param name="MaskedColumns">The columns the mask map names for this table, existing or not.</param>
/// <param name="HashedColumns">The columns the hash map names for this table, existing or not.</param>
public sealed record SqlSnapshotTable(
    string Name,
    string? Schema,
    IReadOnlyList<string> Columns,
    long RowCount,
    IReadOnlyList<JsonObject> Rows,
    IReadOnlyList<string> MaskedColumns,
    IReadOnlyList<string> HashedColumns);
