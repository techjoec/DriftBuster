using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

namespace DriftBuster.Backend.Sql;

/// <summary>Anonymised SQLite exports: every table (or the chosen ones), its schema, row count and rows with masked or hashed columns.</summary>
public static class SqliteSnapshots
{
    /// <summary>The database opened read-only; tables in name order (<c>sqlite_</c> internal tables skipped).</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="SqliteException">SQLite refuses the file or a statement.</exception>
    public static SqlSnapshot Build(string path, SqlExportSettings settings, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Database not found: {path}", path);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        var tables = new List<SqlSnapshotTable>();
        foreach (var (name, schema) in Tables(connection))
        {
            if ((settings.Tables.Count == 0 || settings.Tables.Contains(name, StringComparer.Ordinal)) && !settings.ExcludeTables.Contains(name, StringComparer.Ordinal))
            {
                tables.Add(ExportTable(connection, name, schema, settings));
            }
        }

        return new SqlSnapshot(Path.GetFileName(path), "sqlite", (time ?? TimeProvider.System).GetUtcNow(), path, tables);
    }

    /// <summary><c>sha256:</c> and the lower-case hex SHA-256 of the UTF-8 salt followed by the value's compact JSON.</summary>
    public static string HashValue(JsonNode? value, string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        var bytes = Encoding.UTF8.GetBytes(salt + (value?.ToJsonString() ?? "null"));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static List<(string Name, string? Schema)> Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
        using var reader = command.ExecuteReader();
        var tables = new List<(string, string?)>();
        while (reader.Read())
        {
            tables.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetValue(1) as string));
        }

        return tables;
    }

    private static SqlSnapshotTable ExportTable(SqliteConnection connection, string table, string? schema, SqlExportSettings settings)
    {
        var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var masked = settings.MaskedColumns.GetValueOrDefault(table) ?? [];
        var hashed = settings.HashedColumns.GetValueOrDefault(table) ?? [];
        var rows = new List<JsonObject>();
        List<string> columns;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT * FROM {quoted}" + (settings.Limit is { } limit ? $" LIMIT {limit}" : string.Empty);
            using var reader = command.ExecuteReader();
            columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var index = 0; index < columns.Count; index++)
                {
                    var column = columns[index];
                    var value = Value(reader.GetValue(index));
                    row[column] = masked.Contains(column, StringComparer.Ordinal)
                        ? settings.Placeholder
                        : hashed.Contains(column, StringComparer.Ordinal) ? HashValue(value, $"{table}.{column}:{settings.HashSalt}") : value;
                }

                rows.Add(row);
            }
        }

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {quoted}";
        return new SqlSnapshotTable(table, schema, columns, (long)count.ExecuteScalar()!, rows, masked, hashed);
    }

    // A value by its storage class: INTEGER, REAL, TEXT, NULL, or a BLOB as base64.
    private static JsonNode? Value(object value) => value switch
    {
        DBNull => null,
        long integer => integer,
        double real => real,
        string text => text,
        byte[] blob => new JsonObject { ["type"] = "base64", ["value"] = Convert.ToBase64String(blob) },
        _ => value.ToString(),
    };
}
