namespace DriftBuster.Backend.Sql;

/// <summary><c>driftbuster.sql.snapshots.SnapshotTable</c>: one exported table.</summary>
/// <param name="Name">The table's name in <c>sqlite_master</c>.</param>
/// <param name="Schema">Its <c>sql</c> text from <c>sqlite_master</c>, null when there is none or it is stored as a BLOB (<see cref="SchemaBytes"/>).</param>
/// <param name="Columns">The names <c>PRAGMA table_info</c> lists, in order.</param>
/// <param name="RowCount">Every row in the table, however many were exported.</param>
/// <param name="Rows">Each exported row: column name to masked, hashed or normalised value, in <paramref name="Columns"/> order.</param>
/// <param name="MaskedColumns">The columns the mask map names for this table, existing or not.</param>
/// <param name="HashedColumns">The columns the hash map names for this table, existing or not.</param>
public sealed record SnapshotTable(
    string Name,
    string? Schema,
    IReadOnlyList<string> Columns,
    long RowCount,
    IReadOnlyList<OrderedDictionary<string, object?>> Rows,
    IReadOnlyList<string> MaskedColumns,
    IReadOnlyList<string> HashedColumns)
{
    /// <summary>
    /// The <c>sql</c> value when a writable schema stored it as a BLOB: Python's <c>SnapshotTable.schema</c> is then <c>bytes</c>, which
    /// <see cref="ToDict"/> carries so that <c>json.dumps</c> of the snapshot raises its <c>TypeError</c> where Python's does.
    /// </summary>
    public byte[]? SchemaBytes { get; init; }

    /// <summary><c>to_dict()</c>: name, schema, columns, row_count, rows, masked_columns, hashed_columns, as fresh containers.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["name"] = Name,
        ["schema"] = SchemaBytes ?? (object?)Schema,
        ["columns"] = Columns.Cast<object?>().ToList(),
        ["row_count"] = RowCount,
        ["rows"] = Rows.Select(object? (row) => new OrderedDictionary<string, object?>(row, StringComparer.Ordinal)).ToList(),
        ["masked_columns"] = MaskedColumns.Cast<object?>().ToList(),
        ["hashed_columns"] = HashedColumns.Cast<object?>().ToList(),
    };
}
