using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Remote;

/// <summary><c>driftbuster capture export-sql</c> and <c>driftbuster sql-export</c>.</summary>
public static partial class CaptureRunner
{
    /// <summary>
    /// Creates the output directory, exports each database with <see cref="SqliteSnapshots.BuildSqliteSnapshot"/> to the first free
    /// <c>{stem}-sql-snapshot[-N].json</c> (stem: the prefix or the database name; <c>{prefix}-{stem}</c> for several databases), reports
    /// each on <paramref name="stdout"/>, then writes the manifest (<c>captured_at</c>, exports, options). A missing database or failed
    /// export goes to <paramref name="stderr"/>, sets exit code 1 and moves on.
    /// </summary>
    /// <remarks>
    /// A table whose schema SQLite stores as a BLOB builds, but serialising it throws <see cref="NotSupportedException"/> outside the
    /// export's error handling, ending the command without a manifest.
    /// </remarks>
    public static SqlExportOutcome RunSqlExport(SqlExportOptions options, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var outputDir = EnginePath.Resolve(EnginePath.ExpandUser(options.OutputDir));
        EnginePath.MakeDirectories(outputDir);

        var maskMap = ParseColumnArguments(options.MaskColumn);
        var hashMap = ParseColumnArguments(options.HashColumn);
        var tables = options.Table.ToList();
        var excludeTables = options.ExcludeTable.ToList();
        var hashSalt = string.IsNullOrEmpty(options.HashSalt) ? string.Empty : options.HashSalt;

        var exports = new List<object?>();
        var written = new List<string>();
        var exitCode = 0;
        foreach (var database in options.Database)
        {
            var dbPath = EnginePath.Resolve(EnginePath.ExpandUser(database));
            if (!RunProfileStore.Exists(dbPath))
            {
                stderr.Write($"error: database not found: {dbPath}\n");
                exitCode = 1;
                continue;
            }

            if (LimitRefused(options, stderr))
            {
                exitCode = 1;
                continue;
            }

            SqlSnapshot snapshot;
            try
            {
                snapshot = SqliteSnapshots.BuildSqliteSnapshot(dbPath, tables, excludeTables, maskMap, hashMap, options.Limit, options.Placeholder, hashSalt);
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                stderr.Write($"error: failed to export {dbPath}: {exc.Message}\n");
                exitCode = 1;
                continue;
            }

            var destination = DetermineSnapshotPath(outputDir, SnapshotStem(options, dbPath));
            WriteText(destination, SqliteSnapshots.SnapshotJson(snapshot));
            exports.Add(ExportEntry(dbPath, destination, snapshot, maskMap, hashMap));
            written.Add(destination);
            stdout.Write($"Exported SQL snapshot to {destination}\n");
        }

        var manifestPath = LexicalPath.Join(outputDir, options.ManifestName);
        var manifest = SqlManifest(exports, tables, excludeTables, maskMap, hashMap, options, hashSalt);
        WriteJsonText(manifestPath, manifest);
        if (options.ReportManifestPath)
        {
            stdout.Write($"Manifest written to {manifestPath}\n");
        }

        return new SqlExportOutcome(exitCode, manifestPath, manifest, written);
    }

    // driftbuster sql-export refuses a limit of zero or less after the database is found and before it is exported.
    private static bool LimitRefused(SqlExportOptions options, TextWriter stderr)
    {
        if (!options.LimitMustBePositive || options.Limit is not { } limit || limit > 0)
        {
            return false;
        }

        stderr.Write("error: --limit must be positive when provided\n");
        return true;
    }

    /// <summary><c>{stem}-sql-snapshot.json</c>, or the first <c>{stem}-sql-snapshot-{n}.json</c> (n from 1) that does not exist.</summary>
    public static string DetermineSnapshotPath(string outputDir, string stem)
    {
        ArgumentNullException.ThrowIfNull(outputDir);
        var candidate = LexicalPath.Join(outputDir, $"{stem}-sql-snapshot.json");
        for (var counter = 1; RunProfileStore.Exists(candidate); counter++)
        {
            candidate = LexicalPath.Join(outputDir, $"{stem}-sql-snapshot-{counter}.json");
        }

        return candidate;
    }

    /// <summary>
    /// Non-empty <c>table.column</c> entries split at the first dot and trimmed; entries with an empty side skipped. Tables keep
    /// first-seen order, columns their order.
    /// </summary>
    public static OrderedDictionary<string, IReadOnlyList<string>> ParseColumnArguments(IEnumerable<string?>? values)
    {
        var mapping = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in values ?? [])
        {
            var dot = string.IsNullOrEmpty(entry) ? -1 : entry.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                continue;
            }

            var table = EngineText.Strip(entry![..dot]);
            var column = EngineText.Strip(entry[(dot + 1)..]);
            if (table.Length == 0 || column.Length == 0)
            {
                continue;
            }

            if (!mapping.TryGetValue(table, out var columns))
            {
                mapping[table] = columns = [];
            }

            columns.Add(column);
        }

        var result = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (table, columns) in mapping)
        {
            result[table] = columns;
        }

        return result;
    }

    // The prefix or the database stem; with several databases, "{prefix}-{stem}" (or the stem without a prefix).
    private static string SnapshotStem(SqlExportOptions options, string dbPath)
    {
        var name = PathText.Name(dbPath);
        var stem = name[..(name.Length - PathText.Suffix(dbPath).Length)];
        if (options.Database.Count > 1)
        {
            return string.IsNullOrEmpty(options.Prefix) ? stem : $"{options.Prefix}-{stem}";
        }

        return string.IsNullOrEmpty(options.Prefix) ? stem : options.Prefix;
    }

    private static OrderedDictionary<string, object?> ExportEntry(
        string dbPath,
        string destination,
        SqlSnapshot snapshot,
        OrderedDictionary<string, IReadOnlyList<string>> maskMap,
        OrderedDictionary<string, IReadOnlyList<string>> hashMap)
    {
        var rowCounts = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var table in snapshot.Tables)
        {
            rowCounts[table.Name] = table.RowCount;
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = dbPath,
            ["output"] = PathText.Name(destination),
            ["dialect"] = "sqlite",
            ["tables"] = snapshot.Tables.Select(object? (table) => table.Name).ToList(),
            ["row_counts"] = rowCounts,
            ["masked_columns"] = ColumnMapPayload(maskMap),
            ["hashed_columns"] = ColumnMapPayload(hashMap),
        };
    }

    private static OrderedDictionary<string, object?> SqlManifest(
        List<object?> exports,
        List<string> tables,
        List<string> excludeTables,
        OrderedDictionary<string, IReadOnlyList<string>> maskMap,
        OrderedDictionary<string, IReadOnlyList<string>> hashMap,
        SqlExportOptions options,
        string hashSalt)
        => new(StringComparer.Ordinal)
        {
            ["captured_at"] = IsoTimestamp.Format(UtcNow()),
            ["exports"] = exports,
            ["options"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tables"] = tables.Cast<object?>().ToList(),
                ["exclude_tables"] = excludeTables.Cast<object?>().ToList(),
                ["masked_columns"] = ColumnMapPayload(maskMap),
                ["hashed_columns"] = ColumnMapPayload(hashMap),
                ["limit"] = options.Limit is { } limit ? EngineValues.Narrow(limit) : null,
                ["placeholder"] = options.Placeholder,
                ["hash_salt"] = hashSalt,
            },
        };

    private static OrderedDictionary<string, object?> ColumnMapPayload(OrderedDictionary<string, IReadOnlyList<string>> map)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (table, columns) in map)
        {
            payload[table] = columns.Cast<object?>().ToList();
        }

        return payload;
    }
}
