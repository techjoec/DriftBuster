using System.Globalization;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace DriftBuster.Backend.Sql;

/// <summary>Anonymised SQLite snapshots.</summary>
public static partial class SqliteSnapshots
{
    public const string DefaultPlaceholder = "[REDACTED]";

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The capture clock for <see cref="SqlSnapshot.CapturedAt"/> (test seam).</summary>
    internal static Func<DateTimeOffset> UtcNow { get; set; } = IsoTimestamp.UtcNow;

    /// <summary>
    /// Every table in <c>sqlite_master</c> (name order, names starting <c>sqlite_</c> skipped) that the non-empty <paramref name="tables"/>
    /// names, and <paramref name="excludeTables"/> does not, is exported: its <c>PRAGMA table_info</c> columns, its first
    /// <paramref name="limit"/> rows of <c>SELECT *</c> in the order SQLite yields them, and its <c>COUNT(*)</c>. The table name is spliced
    /// in unquoted, so a name SQL cannot read unquoted raises what SQLite reports. A masked column holds <paramref name="placeholder"/>
    /// (mask wins over hash), a hashed column <see cref="HashText"/> salted with <c>{table}.{column}:{hash_salt}</c>, any other column
    /// <see cref="NormaliseValue"/>. <paramref name="limit"/> may be null, an integer of any size, a float or a bool: <c>limit &lt;= 0</c> is
    /// refused up front (a string or list raises <see cref="InvalidDataException"/>); each exported table converts it to an integer (a NaN
    /// raises <see cref="InvalidDataException"/>, an infinity <see cref="OverflowException"/>, only once a table is reached).
    /// </summary>
    /// <remarks>
    /// The database is opened read-only through <see cref="SqliteConnectionStringBuilder"/>, never as a file URI. Each value keeps its
    /// per-row storage class.
    /// </remarks>
    public static SqlSnapshot BuildSqliteSnapshot(
        string path,
        IEnumerable<string?>? tables = null,
        IEnumerable<string?>? excludeTables = null,
        object? maskColumns = null,
        object? hashColumns = null,
        object? limit = null,
        string? placeholder = DefaultPlaceholder,
        string hashSalt = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(hashSalt);
        if (limit is not null && EngineValues.LessThanOrEqual(limit, 0))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be positive when provided.");
        }

        var resolved = LexicalPath.Str(path);
        if (!RunProfileStore.Exists(resolved))
        {
            throw new FileNotFoundException($"Database not found: {resolved}", resolved);
        }

        var include = (tables ?? []).Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.Ordinal);
        var excluded = (excludeTables ?? []).Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.Ordinal);
        var export = new TableExport(
            SqlColumnMap.ParseColumnMap(maskColumns),
            SqlColumnMap.ParseColumnMap(hashColumns),
            limit,
            placeholder,
            hashSalt);

        var snapshots = new List<SnapshotTable>();
        using (var connection = Open(resolved))
        {
            var db = connection.Handle!;
            foreach (var (name, schema) in IterTables(db))
            {
                if ((include.Count > 0 && !include.Contains(name)) || excluded.Contains(name))
                {
                    continue;
                }

                snapshots.Add(ExportTable(db, name, schema, export));
            }
        }

        var capturedAt = IsoTimestamp.Format(UtcNow());
        return new SqlSnapshot(PathText.Name(resolved), "sqlite", capturedAt, resolved, snapshots);
    }

    /// <summary>
    /// <see cref="BuildSqliteSnapshot"/>, then the snapshot written as <see cref="SnapshotJson"/>: indented, ASCII-escaped JSON with keys
    /// in code point order, the platform's line breaks, no trailing newline.
    /// </summary>
    public static SqlSnapshot WriteSqliteSnapshot(
        string path,
        string destination,
        IEnumerable<string?>? tables = null,
        IEnumerable<string?>? excludeTables = null,
        object? maskColumns = null,
        object? hashColumns = null,
        object? limit = null,
        string? placeholder = DefaultPlaceholder,
        string hashSalt = "")
    {
        ArgumentNullException.ThrowIfNull(destination);
        var snapshot = BuildSqliteSnapshot(path, tables, excludeTables, maskColumns, hashColumns, limit, placeholder, hashSalt);
        EngineTextFile.WriteText(destination, SnapshotJson(snapshot));
        return snapshot;
    }

    /// <summary>The snapshot as indented, sorted, ASCII-escaped JSON with the platform's line breaks.</summary>
    internal static string SnapshotJson(SqlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = Canonicaliser.DumpsSorted(snapshot.ToDict(), indent: true, ensureAscii: true);
        return string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal)
            ? text
            : text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name</c> (binary collation, so UTF-8 byte order), skipping names
    /// that start with <c>sqlite_</c> (case-sensitive). A schema is the <c>sql</c> value as read: text, or bytes where a writable schema
    /// stored a BLOB. The rows are fetched up front and yielded one at a time, so every table before a refused row is exported first.
    /// </summary>
    internal static IEnumerable<(string Name, object? Schema)> IterTables(sqlite3 db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return IterTablesCore(Sqlite3Cursor.FetchAll(db, "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name").Rows);
    }

    private static IEnumerable<(string Name, object? Schema)> IterTablesCore(IEnumerable<object?[]> rows)
    {
        foreach (var row in rows)
        {
            if (row[0] is byte[])
            {
                throw new InvalidDataException("A table name stored as a BLOB cannot be exported.");
            }

            var name = (string)row[0]!;
            if (!name.StartsWith("sqlite_", StringComparison.Ordinal))
            {
                yield return (name, row[1]);
            }
        }
    }

    // Opened read-only, a directory would open and fail on the first read with an I/O error, so it is refused before opening with
    // SQLite's own SQLITE_CANTOPEN text. That text is read before any connection has loaded the native provider, so the provider is
    // loaded first.
    private static SqliteConnection Open(string resolved)
    {
        if (string.Equals(resolved, InMemoryName, StringComparison.Ordinal))
        {
            // ":memory:" opens a new empty in-memory database whatever file of that name exists (Linux only: Windows file names cannot hold
            // ":"), so the export lists no tables.
            return OpenInMemory();
        }

        if (RunProfileStore.IsDirectory(resolved))
        {
            Batteries_V2.Init();
            throw new SqliteException(raw.sqlite3_errstr(raw.SQLITE_CANTOPEN).utf8_to_string(), raw.SQLITE_CANTOPEN);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = EnginePath.Absolute(EnginePath.KernelPath(resolved)),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException exc)
        {
            connection.Dispose();
            var primary = exc.SqliteErrorCode & 0xFF;
            throw new SqliteException(raw.sqlite3_errstr(primary).utf8_to_string(), exc.SqliteErrorCode, exc.SqliteExtendedErrorCode);
        }
    }

    private const string InMemoryName = ":memory:";

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = InMemoryName, Mode = SqliteOpenMode.Memory, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    // A schema stored as a BLOB stays bytes (SnapshotTable.SchemaBytes): only writing the snapshot as JSON refuses it.
    private static SnapshotTable ExportTable(sqlite3 db, string tableName, object? schema, TableExport export)
    {
        var info = Sqlite3Cursor.FetchAll(db, $"PRAGMA table_info({tableName})");
        var columns = info.Rows.Select(row => (string)row[1]!).ToList();
        // Converted for each table, after its PRAGMA.
        var limitClause = export.Limit is { } limit ? " LIMIT " + EngineBuiltins.Int(limit).ToString(CultureInfo.InvariantCulture) : string.Empty;
        var fetched = Sqlite3Cursor.FetchAll(db, $"SELECT * FROM {tableName}{limitClause}");
        var masked = export.MaskMap.GetValueOrDefault(tableName, []);
        var hashed = export.HashMap.GetValueOrDefault(tableName, []);
        var rows = new List<OrderedDictionary<string, object?>>();
        foreach (var row in fetched.Rows)
        {
            var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var value = Sqlite3Cursor.Lookup(fetched, row, column);
                payload[column] = masked.Contains(column, StringComparer.Ordinal)
                    ? export.Placeholder
                    : hashed.Contains(column, StringComparer.Ordinal)
                        ? HashText(value, $"{tableName}.{column}:{export.HashSalt}")
                        : NormaliseValue(value);
            }

            rows.Add(payload);
        }

        var count = Sqlite3Cursor.FetchAll(db, $"SELECT COUNT(*) FROM {tableName}");
        return new SnapshotTable(tableName, schema as string, columns, (long)count.Rows[0][0]!, rows, masked.ToList(), hashed.ToList())
        {
            SchemaBytes = schema as byte[],
        };
    }

    private sealed record TableExport(
        OrderedDictionary<string, IReadOnlyList<string>> MaskMap,
        OrderedDictionary<string, IReadOnlyList<string>> HashMap,
        object? Limit,
        string? Placeholder,
        string HashSalt);
}
