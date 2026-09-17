using System.Security.Cryptography;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// The <c>sql_snapshot</c> branch of <c>offline_runner.execute_config</c>: runs one <see cref="OfflineSqlSnapshotSource"/> into its
/// destination directory and returns the manifest source summary, the <c>sql_exports</c> metadata entry and the file it wrote.
/// </summary>
public static class SqlSnapshotCollector
{
    internal const string ResultFileName = "sql-snapshot.json";

    /// <summary>
    /// The database is <c>os.path.expanduser(os.path.expandvars(path))</c>, joined under <paramref name="baseDir"/> when relative (the
    /// expanded path itself when that does not exist and the expanded path does). A missing database is skipped with reason
    /// <c>missing</c> when the source is optional, else raises <c>FileNotFoundError("SQL snapshot source not found: {path}")</c>. Otherwise
    /// <see cref="SqliteSnapshots.BuildSqliteSnapshot"/> runs with the source's keyword arguments, its payload is written as
    /// <c>json.dumps(payload, indent=2, sort_keys=True)</c> (bytes, no line-break translation) to <see cref="ResultFileName"/> under
    /// <paramref name="destinationRoot"/>, and the summary and metadata carry the table names, row counts, column maps, placeholder,
    /// salt and output name.
    /// </summary>
    /// <exception cref="FileNotFoundException">The database is missing and the source is not optional.</exception>
    public static SqlSnapshotCollection Collect(OfflineSqlSnapshotSource source, string destinationRoot, string alias, string? baseDir = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationRoot);
        ArgumentNullException.ThrowIfNull(alias);
        log ??= _ => { };

        var candidateRaw = LexicalPath.Str(EngineOsPath.ExpandUser(EngineOsPath.ExpandVars(source.Path)));
        var candidate = baseDir is null || LexicalPath.IsAbsolute(candidateRaw)
            ? candidateRaw
            : EngineOsPath.ExpandUser(LexicalPath.Join(baseDir, candidateRaw));
        if (!RunProfileStore.Exists(candidate) && RunProfileStore.Exists(candidateRaw))
        {
            candidate = candidateRaw;
        }

        if (!RunProfileStore.Exists(candidate))
        {
            if (source.Optional)
            {
                log($"optional sql snapshot skipped: {source.Path}");
                var skipped = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "sql_snapshot",
                    ["path"] = source.Path,
                    ["alias"] = alias,
                    ["optional"] = true,
                    ["skipped"] = true,
                    ["reason"] = "missing",
                };
                return new SqlSnapshotCollection(skipped, null, null, 0, null);
            }

            log($"sql snapshot source missing: {source.Path}");
            throw new FileNotFoundException($"SQL snapshot source not found: {source.Path}", source.Path);
        }

        log($"building sql snapshot from {candidate}");
        var snapshot = SqliteSnapshots.BuildSqliteSnapshot(
            candidate,
            source.Tables.Count > 0 ? source.Tables : null,
            source.ExcludeTables.Count > 0 ? source.ExcludeTables : null,
            OfflineSqlSnapshotSource.ColumnMap(source.MaskColumns),
            OfflineSqlSnapshotSource.ColumnMap(source.HashColumns),
            source.Limit,
            source.Placeholder,
            source.HashSalt);
        var encoded = SqliteSnapshots.Utf8.GetBytes(Canonicaliser.DumpsSorted(snapshot.ToDict(), indent: true, ensureAscii: true));
        var snapshotPath = LexicalPath.Join(destinationRoot, ResultFileName);
        EngineTextFile.WriteBytes(snapshotPath, encoded);
        log($"sql snapshot exported with {snapshot.Tables.Count} table(s)");
        return new SqlSnapshotCollection(
            Describe(source, snapshot, alias, metadata: false),
            Describe(source, snapshot, alias, metadata: true),
            snapshotPath,
            encoded.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(encoded)));
    }

    // The manifest source summary, or the sql_exports metadata entry, in execute_config's key order.
    private static OrderedDictionary<string, object?> Describe(OfflineSqlSnapshotSource source, SqlSnapshot snapshot, string alias, bool metadata)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (metadata)
        {
            entry["alias"] = alias;
            entry["source"] = source.Path;
        }
        else
        {
            entry["type"] = "sql_snapshot";
            entry["path"] = source.Path;
            entry["alias"] = alias;
        }

        entry["dialect"] = source.Dialect;
        entry["tables"] = snapshot.Tables.Select(object? (table) => table.Name).ToList();
        entry["row_counts"] = new OrderedDictionary<string, object?>(
            snapshot.Tables.Select(table => new KeyValuePair<string, object?>(table.Name, table.RowCount)), StringComparer.Ordinal);
        entry["masked_columns"] = OfflineSqlSnapshotSource.ColumnMap(source.MaskColumns);
        entry["hashed_columns"] = OfflineSqlSnapshotSource.ColumnMap(source.HashColumns);
        if (metadata)
        {
            entry["placeholder"] = source.Placeholder;
            entry["hash_salt"] = source.HashSalt;
            entry["output"] = ResultFileName;
        }

        return entry;
    }
}
