using System.Globalization;
using System.Text;

using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Models;

using Microsoft.Data.Sqlite;

namespace DriftBuster.Backend.History;

/// <summary>
/// Every scan's settings, kept so the user can ask where else a setting is set or a value appears, and how a setting changed
/// over time. Each distinct copy of a file (its settings and values) is stored once and runs point at it, so a run whose files
/// did not change costs a few rows. Masked values are stored as their fingerprint only. Nothing is ever pruned automatically;
/// <see cref="Clear"/> removes everything.
/// </summary>
public sealed class HistoryStore
{
    public const string FileName = "history.db";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS runs (id INTEGER PRIMARY KEY, recorded_at TEXT NOT NULL, host_set TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS versions (id INTEGER PRIMARY KEY, fingerprint TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS version_settings (
            version_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, key TEXT NOT NULL, value TEXT, value_hash TEXT NOT NULL, masked INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS copies (run_id INTEGER NOT NULL, host_label TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, version_id INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS version_settings_key ON version_settings (key);
        CREATE INDEX IF NOT EXISTS version_settings_hash ON version_settings (value_hash);
        CREATE INDEX IF NOT EXISTS copies_version ON copies (version_id);
        CREATE INDEX IF NOT EXISTS copies_path ON copies (path);
        """;

    private const string EntryColumns = "c.run_id, r.recorded_at, c.host_label, c.path, s.key, s.value, s.masked, s.value_hash";

    private const string EntryJoin = " FROM version_settings s JOIN copies c ON c.version_id = s.version_id JOIN runs r ON r.id = c.run_id";

    // The latest run in which each (server, file) pair was recorded.
    private const string LatestCopies = " AND c.run_id = (SELECT MAX(c2.run_id) FROM copies c2 WHERE c2.host_label = c.host_label AND c2.path = c.path)";

    public HistoryStore(string path)
    {
        DatabasePath = path ?? throw new ArgumentNullException(nameof(path));
    }

    public static string DefaultPath => System.IO.Path.Combine(DriftbusterPaths.GetDataRoot(), FileName);

    public string DatabasePath { get; }

    /// <summary>Records one scan's comparison as a run; returns the run id.</summary>
    public long Record(SettingsComparison comparison, string hostSetId, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(hostSetId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var runId = Scalar(connection, transaction, "INSERT INTO runs (recorded_at, host_set) VALUES ($at, $set); SELECT last_insert_rowid();",
            ("$at", recordedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)), ("$set", hostSetId));
        var labels = comparison.Hosts.ToDictionary(host => host.HostId, host => host.Label, StringComparer.Ordinal);
        foreach (var file in comparison.Files)
        {
            foreach (var presence in file.Presence.Where(value => value.State == SettingValueState.Value))
            {
                var settings = file.Settings
                    .Select(row => (row.Key, Value: row.Values.FirstOrDefault(value => string.Equals(value.HostId, presence.HostId, StringComparison.Ordinal))))
                    .Where(pair => pair.Value is { State: SettingValueState.Value })
                    .Select(pair => (pair.Key, Stored: pair.Value!.Masked ? null : pair.Value.Value, Hash: pair.Value.ValueHash ?? CurationTarget.ValueHashOf(pair.Value.Value ?? string.Empty), pair.Value.Masked))
                    .ToList();
                var versionId = VersionFor(connection, transaction, settings);
                Execute(connection, transaction, "INSERT INTO copies (run_id, host_label, path, version_id) VALUES ($run, $host, $path, $version)",
                    ("$run", runId), ("$host", labels.TryGetValue(presence.HostId, out var label) ? label : presence.HostId), ("$path", file.Path), ("$version", versionId));
            }
        }

        transaction.Commit();
        return runId;
    }

    /// <summary>Every recorded value of one setting in one file, newest first.</summary>
    public IReadOnlyList<HistoryEntry> SettingHistory(string path, string key)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(key);
        return Query($"SELECT {EntryColumns}{EntryJoin} WHERE c.path = $path AND s.key = $key ORDER BY c.run_id DESC, c.host_label",
            ("$path", path), ("$key", key));
    }

    /// <summary>Where else the setting is set: the same key in any file, as each server last had it.</summary>
    public IReadOnlyList<HistoryEntry> WhereSettingIsSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Query($"SELECT {EntryColumns}{EntryJoin} WHERE s.key = $key{LatestCopies} ORDER BY c.path, c.host_label", ("$key", key));
    }

    /// <summary>Where else the value appears: any setting in any file holding the same fingerprint, as each server last had it.</summary>
    public IReadOnlyList<HistoryEntry> WhereValueAppears(string valueHash)
    {
        ArgumentNullException.ThrowIfNull(valueHash);
        return Query($"SELECT {EntryColumns}{EntryJoin} WHERE s.value_hash = $hash{LatestCopies} ORDER BY c.path, s.key, c.host_label", ("$hash", valueHash));
    }

    public HistoryStats Stats()
    {
        if (!File.Exists(DatabasePath))
        {
            return new HistoryStats(0, 0, 0);
        }

        using var connection = Open();
        var runs = (int)Scalar(connection, null, "SELECT COUNT(*) FROM runs");
        var versions = (int)Scalar(connection, null, "SELECT COUNT(*) FROM versions");
        return new HistoryStats(runs, versions, new FileInfo(DatabasePath).Length);
    }

    /// <summary>Removes every recorded run.</summary>
    public void Clear()
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM copies; DELETE FROM version_settings; DELETE FROM versions; DELETE FROM runs;");
        Execute(connection, null, "VACUUM;");
    }

    private SqliteConnection Open()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, null, Schema);
        return connection;
    }

    private static long VersionFor(SqliteConnection connection, SqliteTransaction transaction, List<(string Key, string? Stored, string Hash, bool Masked)> settings)
    {
        var text = new StringBuilder();
        foreach (var (key, _, hash, masked) in settings)
        {
            text.Append(key).Append('\0').Append(hash).Append('\0').Append(masked ? '1' : '0').Append('\n');
        }

        var fingerprint = CurationTarget.ValueHashOf(text.ToString());
        using (var find = Command(connection, transaction, "SELECT id FROM versions WHERE fingerprint = $fingerprint", ("$fingerprint", fingerprint)))
        {
            if (find.ExecuteScalar() is long existing)
            {
                return existing;
            }
        }

        var versionId = Scalar(connection, transaction, "INSERT INTO versions (fingerprint) VALUES ($fingerprint); SELECT last_insert_rowid();", ("$fingerprint", fingerprint));
        using var insert = Command(connection, transaction,
            "INSERT INTO version_settings (version_id, ordinal, key, value, value_hash, masked) VALUES ($version, $ordinal, $key, $value, $hash, $masked)",
            ("$version", versionId), ("$ordinal", 0), ("$key", string.Empty), ("$value", DBNull.Value), ("$hash", string.Empty), ("$masked", 0));
        for (var index = 0; index < settings.Count; index++)
        {
            var (key, stored, hash, masked) = settings[index];
            insert.Parameters["$ordinal"].Value = index;
            insert.Parameters["$key"].Value = key;
            insert.Parameters["$value"].Value = (object?)stored ?? DBNull.Value;
            insert.Parameters["$hash"].Value = hash;
            insert.Parameters["$masked"].Value = masked ? 1 : 0;
            insert.ExecuteNonQuery();
        }

        return versionId;
    }

    private List<HistoryEntry> Query(string sql, params (string Name, object Value)[] parameters)
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        using var connection = Open();
        using var command = Command(connection, null, sql, parameters);
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) != 0,
                reader.GetString(7)));
        }

        return entries;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
