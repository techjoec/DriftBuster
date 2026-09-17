using System.Numerics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// The SQL snapshot port against CPython 3.13's <c>driftbuster.sql.snapshots</c> (<see cref="SqlOracleData"/>): column map parsing,
/// value normalisation, salted hashing, and whole snapshots of generated databases (storage classes, text that is not UTF-8, NaN bits,
/// odd table and column names, internal tables, virtual tables, path forms and error texts), plus the JSON text
/// <c>write_sqlite_snapshot</c> writes.
/// </summary>
public sealed class SqlOracleTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-oracle-");

    public static TheoryData<int> ParseColumnMapCases => SqlOracleData.Indexes("parse_column_map");

    public static TheoryData<int> NormaliseValueCases => SqlOracleData.Indexes("normalise_value");

    public static TheoryData<int> HashTextCases => SqlOracleData.Indexes("hash_text");

    public static TheoryData<int> DatabaseCases => SqlOracleData.Indexes("databases");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Theory]
    [MemberData(nameof(ParseColumnMapCases))]
    public void ParseColumnMapMatchesPython(int index)
    {
        var entry = SqlOracleData.Case("parse_column_map", index);
        OrderedDictionary<string, IReadOnlyList<string>> actual;
        try
        {
            actual = SqlColumnMap.ParseColumnMap(entry["input"]);
        }
        catch (Exception exc) when (entry.ContainsKey("error"))
        {
            SqlOracleData.ErrorMismatch(exc, entry["error"]).Should().BeNull();
            return;
        }

        entry.Should().ContainKey("result", "Python raised {0}", entry.GetValueOrDefault("error"));
        var pairs = actual.Select(object? (item) => new List<object?> { item.Key, item.Value.Cast<object?>().ToList() }).ToList();
        SqlOracleData.Text(pairs).Should().Be(SqlOracleData.Text(entry["result"]));
    }

    [Theory]
    [MemberData(nameof(NormaliseValueCases))]
    public void NormaliseValueMatchesPython(int index)
    {
        var entry = SqlOracleData.Case("normalise_value", index);
        var actual = SqliteSnapshots.NormaliseValue(SqlOracleData.Decode(entry["value"]));
        SqlOracleData.Text(SqlOracleData.Encode(actual)).Should().Be(SqlOracleData.Text(entry["result"]));
    }

    [Theory]
    [MemberData(nameof(HashTextCases))]
    public void HashTextMatchesPython(int index)
    {
        var entry = SqlOracleData.Case("hash_text", index);
        SqliteSnapshots.HashText(SqlOracleData.Decode(entry["value"]), (string)entry["salt"]!).Should().Be((string)entry["result"]!);
    }

    [Theory]
    [MemberData(nameof(DatabaseCases))]
    public void BuildSqliteSnapshotMatchesPython(int index)
    {
        var entry = SqlOracleData.Case("databases", index);
        var path = Prepare(entry);
        var kwargs = (OrderedDictionary<string, object?>)entry["kwargs"]!;
        SqlSnapshot snapshot;
        try
        {
            snapshot = Build(path, kwargs, destination: null);
        }
        catch (Exception exc) when (entry.ContainsKey("error"))
        {
            SqlOracleData.ErrorMismatch(exc, entry["error"], _tmp.FullName).Should().BeNull();
            return;
        }

        entry.Should().ContainKey("result", "Python raised {0}", entry.GetValueOrDefault("error"));
        var expected = (OrderedDictionary<string, object?>)entry["result"]!;
        var expectedPath = (string)expected["path"]!;
        snapshot.Path.Should().Be(PythonPurePath.Str(expectedPath.Replace("{dir}", _tmp.FullName, StringComparison.Ordinal)));
        PythonDateTime.FromIsoFormat(snapshot.CapturedAt).UtcOffset().Should().Be(0);

        var actual = snapshot.ToDict();
        actual.Remove("captured_at");
        actual["path"] = expectedPath;
        SqlOracleData.Text(SqlOracleData.Encode(actual)).Should().Be(SqlOracleData.Text(expected));

        var dumped = snapshot with { CapturedAt = "<captured_at>", Path = expectedPath };
        if (entry.TryGetValue("dump_error", out var dumpError))
        {
            // A BLOB schema: Python builds the snapshot and only json.dumps refuses it.
            var dump = () => Canonicaliser.DumpsSorted(dumped.ToDict(), indent: true, ensureAscii: true);
            SqlOracleData.ErrorMismatch(dump.Should().Throw<Exception>().Which, dumpError).Should().BeNull();
            return;
        }

        Canonicaliser.DumpsSorted(dumped.ToDict(), indent: true, ensureAscii: true).Should().Be((string)entry["dump"]!);
    }

    [Theory]
    [MemberData(nameof(DatabaseCases))]
    public void WriteSqliteSnapshotWritesPythonsJsonText(int index)
    {
        var entry = SqlOracleData.Case("databases", index);
        if (!entry.ContainsKey("dump") && !entry.ContainsKey("dump_error"))
        {
            return;
        }

        var path = Prepare(entry);
        var destination = Path.Combine(_tmp.FullName, "out.json");
        if (entry.TryGetValue("dump_error", out var dumpError))
        {
            // write_sqlite_snapshot raises from json.dumps before the destination is opened.
            var write = () => Build(path, (OrderedDictionary<string, object?>)entry["kwargs"]!, destination);
            SqlOracleData.ErrorMismatch(write.Should().Throw<Exception>().Which, dumpError).Should().BeNull();
            File.Exists(destination).Should().BeFalse();
            return;
        }

        var snapshot = Build(path, (OrderedDictionary<string, object?>)entry["kwargs"]!, destination);

        var written = File.ReadAllText(destination);
        written.Should().Be(SqliteSnapshots.SnapshotJson(snapshot));
        var expectedPath = (string)((OrderedDictionary<string, object?>)entry["result"]!)["path"]!;
        SqliteSnapshots.SnapshotJson(snapshot with { CapturedAt = "<captured_at>", Path = expectedPath })
            .Should().Be(((string)entry["dump"]!).Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private static SqlSnapshot Build(string path, OrderedDictionary<string, object?> kwargs, string? destination)
    {
        var tables = Strings(kwargs.GetValueOrDefault("tables"));
        var excludeTables = Strings(kwargs.GetValueOrDefault("exclude_tables"));
        var limit = kwargs.GetValueOrDefault("limit") is { } bound ? PythonBuiltins.Int(bound) : (BigInteger?)null;
        var placeholder = kwargs.TryGetValue("placeholder", out var text) ? (string?)text : SqliteSnapshots.DefaultPlaceholder;
        var hashSalt = (string?)kwargs.GetValueOrDefault("hash_salt") ?? string.Empty;
        return destination is null
            ? SqliteSnapshots.BuildSqliteSnapshot(
                path, tables, excludeTables, kwargs.GetValueOrDefault("mask_columns"), kwargs.GetValueOrDefault("hash_columns"), limit, placeholder, hashSalt)
            : SqliteSnapshots.WriteSqliteSnapshot(
                path, destination, tables, excludeTables, kwargs.GetValueOrDefault("mask_columns"), kwargs.GetValueOrDefault("hash_columns"), limit, placeholder, hashSalt);
    }

    private static List<string?>? Strings(object? value) => (value as List<object?>)?.Cast<string?>().ToList();

    // Builds the case's database under the temporary directory and returns the path text Python was given.
    private string Prepare(OrderedDictionary<string, object?> entry)
    {
        var target = Path.Combine(_tmp.FullName, "case.sqlite");
        switch ((string?)entry.GetValueOrDefault("kind") ?? "setup")
        {
            case "missing":
                break;
            case "directory":
                Directory.CreateDirectory(target);
                break;
            case "bytes":
                File.WriteAllBytes(target, Convert.FromHexString((string)entry["content"]!));
                break;
            default:
                Directory.CreateDirectory(Path.Combine(_tmp.FullName, "sub"));
                SqlTestDatabase.Build(target, (List<object?>)SqlOracleData.Decode(entry["setup"])!);
                if (entry.GetValueOrDefault("patches") is List<object?> patches)
                {
                    SqlTestDatabase.Patch(
                        target,
                        patches.Cast<OrderedDictionary<string, object?>>()
                            .Select(patch => (Convert.FromHexString((string)patch["find"]!), Convert.FromHexString((string)patch["replace"]!))));
                }

                break;
        }

        return Path.Combine(_tmp.FullName, (string?)entry.GetValueOrDefault("path") ?? "case.sqlite");
    }
}
