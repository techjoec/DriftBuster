using System.CommandLine;
using System.Security.Cryptography;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;

using Microsoft.Data.Sqlite;

namespace DriftBuster.Cli;

/// <summary>The <c>sql-export</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_sql_export</c>).</summary>
public static partial class ParityDump
{
    private const string SqlExportTimestamp = "2025-04-05T06:07:08.090807+00:00";

    private static Command BuildSqlExport()
    {
        var caseArgument = new Argument<string>("case") { Description = "sql-export case JSON file." };
        var scratchOption = new Option<string?>("--scratch") { Description = "Directory created for the run and removed after it." };
        var command = new Command("sql-export", "write_sqlite_snapshot of a database built from the case's SQL script.");
        command.Arguments.Add(caseArgument);
        command.Options.Add(scratchOption);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [SqlExport(parseResult.GetValue(caseArgument)!, parseResult.GetValue(scratchOption))]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds the case's database under <c>&lt;scratch&gt;/work</c>, makes that the working directory and runs
    /// <see cref="SqliteSnapshots.WriteSqliteSnapshot"/> over the case's <c>path</c> into <c>snapshot.json</c> with the case's options and
    /// <see cref="SqliteSnapshots.UtcNow"/> fixed at the case's <c>timestamp</c>: <c>snapshot</c> (<c>to_dict</c>) and <c>file</c> (the written
    /// bytes), or <c>error</c>. The scratch directory is removed afterwards.
    /// </summary>
    internal static string SqlExport(string casePath, string? scratchPath)
    {
        var caseFull = Path.GetFullPath(casePath);
        var testCase = LoadCase(caseFull);
        var scratch = scratchPath ?? Directory.CreateTempSubdirectory("driftbuster-parity-sql-export-").FullName;
        var work = Path.Combine(scratch, "work");
        Directory.CreateDirectory(work);
        var previous = Environment.CurrentDirectory;
        var originalNow = SqliteSnapshots.UtcNow;
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            BuildSqlite(
                Path.Combine(work, (string?)testCase.GetValueOrDefault("file") ?? "case.sqlite"),
                testCase.GetValueOrDefault("database") as IReadOnlyDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal),
                Path.GetDirectoryName(caseFull)!);
            Environment.CurrentDirectory = work;
            var moment = PythonDateTime.FromIsoFormat((string?)testCase.GetValueOrDefault("timestamp") ?? SqlExportTimestamp);
            SqliteSnapshots.UtcNow = () => moment;
            try
            {
                var options = testCase.GetValueOrDefault("options") as IReadOnlyDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                var snapshot = WriteSnapshot((string?)testCase.GetValueOrDefault("path") ?? "case.sqlite", "snapshot.json", options);
                record["snapshot"] = snapshot.ToDict();
                record["file"] = FileEntry("snapshot.json", "snapshot.json", prefix: null);
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                record["error"] = ErrorPayload(exc);
            }
        }
        finally
        {
            SqliteSnapshots.UtcNow = originalNow;
            Environment.CurrentDirectory = previous;
            Directory.Delete(scratch, recursive: true);
        }

        return Keyed(record);
    }

    // write_sqlite_snapshot(path, destination, **options): only the options the case names are passed, so the others keep their defaults.
    private static SqlSnapshot WriteSnapshot(string path, string destination, IReadOnlyDictionary<string, object?> options)
        => SqliteSnapshots.WriteSqliteSnapshot(
            path,
            destination,
            TableNames(options.GetValueOrDefault("tables")),
            TableNames(options.GetValueOrDefault("exclude_tables")),
            options.GetValueOrDefault("mask_columns"),
            options.GetValueOrDefault("hash_columns"),
            options.GetValueOrDefault("limit"),
            options.TryGetValue("placeholder", out var placeholder) ? (string?)placeholder : SqliteSnapshots.DefaultPlaceholder,
            HashSaltText(options));

    // A name entry that is truthy, hashable and not a str: it never equals a table name, so it only makes the set non-empty. SQL text
    // cannot name a table holding a NUL, so the sentinel matches nothing.
    private const string NonStrName = "\0non-str";

    /// <summary>
    /// <c>{name for name in tables or () if name}</c> as the typed name list: a falsy value is nothing; each truthy entry is itself for a
    /// str, <see cref="NonStrName"/> for any other hashable value, and a list or dict raises Python's <c>unhashable type</c>. Iterated by the
    /// export where Python builds the set, so a refused entry raises at the same point (after the limit and existence checks).
    /// </summary>
    internal static IEnumerable<string?>? TableNames(object? value) => PythonBuiltins.IsTruthy(value) ? TableNamesCore(value) : null;

    private static IEnumerable<string?> TableNamesCore(object? value)
    {
        foreach (var name in PythonBuiltins.Iterate(value))
        {
            if (!PythonBuiltins.IsTruthy(name))
            {
                continue;
            }

            _ = PythonValues.HashKeys.GetHashCode(name!);
            yield return name as string ?? NonStrName;
        }
    }

    // hash_salt reaches Python only inside f"{table}.{column}:{hash_salt}", so a case value that is not a str (null, a number) is passed as
    // its str() text ("None"), the text that f-string formats; the typed port takes the salt as text.
    private static string HashSaltText(IReadOnlyDictionary<string, object?> options)
        => options.TryGetValue("hash_salt", out var salt) ? salt as string ?? PythonRepr.Str(salt) : string.Empty;

    // py_dump.py _load: json.loads(Path(case).read_text(encoding="utf-8")) of a mapping.
    private static IReadOnlyDictionary<string, object?> LoadCase(string path)
        => PythonJson.TryLoads(PythonUtf8.Decode(File.ReadAllBytes(path)), out var value) && value is IReadOnlyDictionary<string, object?> mapping
            ? mapping
            : throw new InvalidDataException($"The case file is not a JSON object: {path}");

    /// <summary>
    /// py_dump.py <c>_build_sqlite</c>: <c>script</c> (a file under <paramref name="baseDirectory"/> run as one script on a new database in
    /// autocommit mode), <c>hex</c> (the file's bytes), <c>kind: directory</c> or <c>kind: missing</c>.
    /// </summary>
    internal static void BuildSqlite(string target, IReadOnlyDictionary<string, object?> database, string baseDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (database.GetValueOrDefault("script") is string script)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = PythonUtf8.Decode(File.ReadAllBytes(Path.Combine(baseDirectory, script)));
            command.ExecuteNonQuery();
        }
        else if (database.GetValueOrDefault("hex") is string hex)
        {
            File.WriteAllBytes(target, Convert.FromHexString(hex));
        }
        else if (database.GetValueOrDefault("kind") is "directory")
        {
            Directory.CreateDirectory(target);
        }
        else if (database.GetValueOrDefault("kind") is not "missing")
        {
            throw new InvalidDataException("unsupported database spec");
        }
    }

    /// <summary>
    /// py_dump.py <c>_file_entry</c>: <c>{"path", "size", "sha256", "text"}</c>, the text decoded as UTF-8 with replacement and the working
    /// directory respelled <c>&lt;workdir&gt;</c> when <paramref name="prefix"/> is given.
    /// </summary>
    private static OrderedDictionary<string, object?> FileEntry(string path, string relative, string? prefix)
    {
        var raw = File.ReadAllBytes(path);
        var text = ReplacingUtf8.GetString(raw);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = relative,
            ["size"] = raw.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            ["text"] = prefix is null ? text : text.Replace(prefix, WorkdirToken, StringComparison.Ordinal),
        };
    }
}
