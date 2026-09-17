using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;
using DriftBuster.Backend.Tests.Infrastructure;

using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// Port details the oracle cases do not reach: typed column map inputs, <c>to_dict</c> key order, the <c>sqlite3</c> module's statement
/// checks, row lookup and error classes, the file text, and what the read-only open does to the database and its journal.
/// </summary>
public sealed class SqlSnapshotEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-edge-");

    public void Dispose()
    {
        foreach (var directory in _tmp.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            directory.Attributes = FileAttributes.Normal;
            if (!OperatingSystem.IsWindows())
            {
                directory.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
        }

        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void TypedColumnMapInputsParseLikeTheirPythonForms()
    {
        var listForm = SqlColumnMap.ParseColumnMap(new List<object?> { "a.x", " b . y ", "a.z" });
        Describe(SqlColumnMap.ParseColumnMap(new[] { "a.x", " b . y ", "a.z" })).Should().Be(Describe(listForm));
        Describe(SqlColumnMap.ParseColumnMap(new List<string> { "a.x", " b . y ", "a.z" })).Should().Be(Describe(listForm));
        Describe(SqlColumnMap.ParseColumnMap(new[] { "a.x", " b . y ", "a.z" }.Where(_ => true))).Should().Be(Describe(listForm));
        Describe(listForm).Should().Be("a=x,z;b=y");

        var typed = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["t"] = ["c", " ", " d "],
            [string.Empty] = ["skipped"],
            ["u"] = null!,
        };
        Describe(SqlColumnMap.ParseColumnMap(typed)).Should().Be("t=c, d ;u=None");
        Describe(SqlColumnMap.ParseColumnMap(SqlColumnMap.ParseColumnMap(typed))).Should().Be("t=c, d ;u=None");
    }

    // build_sqlite_snapshot: `limit <= 0` on the value as given, then f" LIMIT {int(limit)}" once a table is reached (CPython 3.13 texts).
    [Fact]
    public void LimitIsComparedRawAndConvertedPerTable()
    {
        var database = SqlTestDatabase.CreateSampleDatabase(Tmp("limits.sqlite"));
        static int Rows(SqlSnapshot snapshot) => snapshot.Tables[0].Rows.Count;

        Rows(SqliteSnapshots.BuildSqliteSnapshot(database, limit: 0.5)).Should().Be(0, "int(0.5) is 0");
        Rows(SqliteSnapshots.BuildSqliteSnapshot(database, limit: true)).Should().Be(1, "int(True) is 1");
        Rows(SqliteSnapshots.BuildSqliteSnapshot(database, limit: new BigInteger(1))).Should().Be(1);

        var text = () => SqliteSnapshots.BuildSqliteSnapshot(database, limit: "2");
        text.Should().Throw<PythonTypeException>().WithMessage("'<=' not supported between instances of 'str' and 'int'");
        var list = () => SqliteSnapshots.BuildSqliteSnapshot(database, limit: new List<object?> { 1 });
        list.Should().Throw<PythonTypeException>().WithMessage("'<=' not supported between instances of 'list' and 'int'");
        var negative = () => SqliteSnapshots.BuildSqliteSnapshot(database, limit: double.NegativeInfinity);
        negative.Should().Throw<PythonValueException>().WithMessage("limit must be positive when provided");
        var infinite = () => SqliteSnapshots.BuildSqliteSnapshot(database, limit: double.PositiveInfinity);
        infinite.Should().Throw<OverflowException>().WithMessage("cannot convert float infinity to integer");
        var nan = () => SqliteSnapshots.BuildSqliteSnapshot(database, limit: double.NaN);
        nan.Should().Throw<PythonValueException>().WithMessage("cannot convert float NaN to integer");

        var empty = Tmp("empty.sqlite");
        SqlTestDatabase.Build(empty, ["PRAGMA user_version = 1"]);
        SqliteSnapshots.BuildSqliteSnapshot(empty, limit: double.NaN).Tables.Should().BeEmpty("int(limit) runs only for an exported table");
    }

    [Fact]
    public void WritingTheSnapshotOverADirectoryRaisesPythonsOSError()
    {
        var database = SqlTestDatabase.CreateSampleDatabase(Tmp("written.sqlite"));
        var destination = Tmp("out.json");
        Directory.CreateDirectory(destination);

        var write = () => SqliteSnapshots.WriteSqliteSnapshot(database, destination + Path.DirectorySeparatorChar);

        var raised = write.Should().Throw<IOException>().Which;
        raised.Message.Should().Be(OSErrorTexts.DirectoryOpen(destination));
        PythonOSError.TypeName(raised.HResult).Should().Be(OSErrorTexts.DirectoryOpenType);
    }

    [Fact]
    public void ToDictKeepsPythonsKeyOrder()
    {
        var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["z"] = 1L, ["a"] = null };
        var table = new SnapshotTable("t", null, ["z", "a"], 3, [row], ["m"], ["h"]);
        var snapshot = new SqlSnapshot("db.sqlite", "sqlite", "2026-01-01T00:00:00+00:00", "/x/db.sqlite", [table]);

        var payload = snapshot.ToDict();
        payload.Keys.Should().Equal("database", "dialect", "captured_at", "path", "tables");
        var tableDict = (OrderedDictionary<string, object?>)((List<object?>)payload["tables"]!)[0]!;
        tableDict.Keys.Should().Equal("name", "schema", "columns", "row_count", "rows", "masked_columns", "hashed_columns");
        var copied = (OrderedDictionary<string, object?>)((List<object?>)tableDict["rows"]!)[0]!;
        copied.Keys.Should().Equal("z", "a");
        copied.Should().NotBeSameAs(row);
    }

    [Fact]
    public void WrittenFileIsUtf8JsonWithoutBomOrTrailingNewline()
    {
        var database = SqlTestDatabase.CreateSampleDatabase(Tmp("sample.sqlite"));
        var destination = Tmp("out.json");

        var snapshot = SqliteSnapshots.WriteSqliteSnapshot(database, destination, hashColumns: new[] { "accounts.email" });

        var bytes = File.ReadAllBytes(destination);
        bytes.Should().OnlyContain(value => value < 0x80, "ensure_ascii output has no BOM and no non-ASCII byte");
        bytes[^1].Should().Be((byte)'}');
        Encoding.UTF8.GetString(bytes).Should().StartWith("{" + Environment.NewLine + "  \"captured_at\": \"" + snapshot.CapturedAt + "\",");
    }

    [Fact]
    public void Sqlite3ExceptionCarriesPythonsClassName()
    {
        new Sqlite3Exception().TypeName.Should().Be("DatabaseError");
        new Sqlite3Exception("m").Message.Should().Be("m");
        var inner = new InvalidOperationException();
        var wrapped = new Sqlite3Exception("m", inner);
        (wrapped.TypeName, wrapped.InnerException, wrapped.SqliteErrorCode).Should().Be(("DatabaseError", inner, (int?)null));
        var full = new Sqlite3Exception("OperationalError", "locked", 5, inner);
        (full.TypeName, full.Message, full.SqliteErrorCode, full.InnerException).Should().Be(("OperationalError", "locked", (int?)5, inner));
    }

    [Fact]
    public void AnUnpairedSurrogateInTheSaltHashesAsTheReplacementCharacter()
        => SqliteSnapshots.HashText("x", "salt\uD800").Should().Be(SqliteSnapshots.HashText("x", "salt\uFFFD"));

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("SELECT 1;", true)]
    [InlineData("SELECT 1; -- trailing comment", true)]
    [InlineData("SELECT 1; \t\f\r\n/* block */ -- line\n", true)]
    [InlineData("SELECT 1; /* unterminated", true)]
    [InlineData("SELECT 1; -- unterminated line", true)]
    [InlineData("SELECT 1; /*/ still open", true)]
    [InlineData("SELECT 1; SELECT 2", false)]
    [InlineData("SELECT 1; /* c */ x", false)]
    [InlineData("SELECT 1; -", false)]
    [InlineData("SELECT 1; /", false)]
    [InlineData("SELECT 1;\vSELECT 2", false)]
    public void OnlyOneStatementMayRun(string sql, bool accepted)
    {
        using var connection = Memory();
        var run = () => Sqlite3Cursor.FetchAll(connection.Handle!, sql);
        if (accepted)
        {
            run().Rows.Should().ContainSingle();
        }
        else
        {
            run.Should().Throw<Sqlite3Exception>()
                .Where(exc => exc.TypeName == "ProgrammingError" && exc.Message == "You can only execute one statement at a time.");
        }
    }

    [Fact]
    public void QueryTextLongerThanTheLibraryLimitIsADataError()
    {
        using var connection = Memory();
        Sqlite3Cursor.FetchAll(connection.Handle!, "SELECT name FROM sqlite_master").Rows.Should().BeEmpty();
        raw.sqlite3_limit(connection.Handle!, 1, 16);
        var run = () => Sqlite3Cursor.FetchAll(connection.Handle!, "SELECT 1 AS a_long_name");
        run.Should().Throw<Sqlite3Exception>().Where(exc => exc.TypeName == "DataError" && exc.Message == "query string is too large");
        Sqlite3Cursor.FetchAll(connection.Handle!, "SELECT 12345678").Rows.Should().ContainSingle();
    }

    [Fact]
    public void RowLookupIgnoresAsciiCaseOnlyForAsciiNames()
    {
        using var connection = Memory();
        var result = Sqlite3Cursor.FetchAll(connection.Handle!, "SELECT 1 AS Ab, 2 AS \"\u00C9\", 3 AS ab");
        var row = result.Rows[0];

        Sqlite3Cursor.Lookup(result, row, "ab").Should().Be(1L);
        Sqlite3Cursor.Lookup(result, row, "AB").Should().Be(1L);
        Sqlite3Cursor.Lookup(result, row, "\u00C9").Should().Be(2L);
        var accented = () => Sqlite3Cursor.Lookup(result, row, "\u00E9");
        accented.Should().Throw<PythonIndexException>().WithMessage("No item with that key");
        var shortRow = () => Sqlite3Cursor.Lookup(result, [1L], "\u00C9");
        shortRow.Should().Throw<PythonIndexException>().WithMessage("tuple index out of range");
    }

    [Theory]
    [InlineData(raw.SQLITE_INTERNAL, "InternalError")]
    [InlineData(raw.SQLITE_NOTFOUND, "InternalError")]
    [InlineData(raw.SQLITE_ERROR, "OperationalError")]
    [InlineData(raw.SQLITE_PERM, "OperationalError")]
    [InlineData(raw.SQLITE_ABORT, "OperationalError")]
    [InlineData(raw.SQLITE_BUSY, "OperationalError")]
    [InlineData(raw.SQLITE_LOCKED, "OperationalError")]
    [InlineData(raw.SQLITE_READONLY, "OperationalError")]
    [InlineData(raw.SQLITE_INTERRUPT, "OperationalError")]
    [InlineData(raw.SQLITE_IOERR, "OperationalError")]
    [InlineData(raw.SQLITE_FULL, "OperationalError")]
    [InlineData(raw.SQLITE_CANTOPEN, "OperationalError")]
    [InlineData(raw.SQLITE_PROTOCOL, "OperationalError")]
    [InlineData(raw.SQLITE_EMPTY, "OperationalError")]
    [InlineData(raw.SQLITE_SCHEMA, "OperationalError")]
    [InlineData(raw.SQLITE_CORRUPT, "DatabaseError")]
    [InlineData(raw.SQLITE_NOTADB, "DatabaseError")]
    [InlineData(raw.SQLITE_TOOBIG, "DataError")]
    [InlineData(raw.SQLITE_CONSTRAINT, "IntegrityError")]
    [InlineData(raw.SQLITE_MISMATCH, "IntegrityError")]
    [InlineData(raw.SQLITE_MISUSE, "InterfaceError")]
    [InlineData(raw.SQLITE_RANGE, "InterfaceError")]
    [InlineData(raw.SQLITE_AUTH, "DatabaseError")]
    public void ResultCodesMapToPythonsExceptionClasses(int code, string typeName)
        => Sqlite3Cursor.ErrorClass(code).Should().Be(typeName);

    [Fact]
    public void AnUnreadableDatabaseIsRefusedAsPythonRefusesIt()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permission bits");
            return;
        }

        var database = SqlTestDatabase.CreateSampleDatabase(Tmp("locked.sqlite"));
        File.SetUnixFileMode(database, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanRead(database), "the process reads files regardless of permission bits");
            var build = () => SqliteSnapshots.BuildSqliteSnapshot(database);
            build.Should().Throw<Sqlite3Exception>()
                .Where(exc => exc.TypeName == "OperationalError" && exc.Message == "unable to open database file" && exc.SqliteErrorCode == raw.SQLITE_CANTOPEN);
        }
        finally
        {
            File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void TheSnapshotLeavesARollbackJournalDatabaseUntouched()
    {
        var database = SqlTestDatabase.CreateSampleDatabase(Tmp("sample.sqlite"));
        var before = File.ReadAllBytes(database);

        SqliteSnapshots.BuildSqliteSnapshot(database, hashColumns: new[] { "accounts.email" }).Tables.Should().ContainSingle();

        File.ReadAllBytes(database).Should().Equal(before);
        Directory.GetFiles(_tmp.FullName).Select(Path.GetFileName).Should().Equal("sample.sqlite");
    }

    [Fact]
    public void AWalDatabaseIsReadWithItsUncheckpointedFrames()
    {
        var database = Tmp("wal.sqlite");
        SqlTestDatabase.Build(database, ["PRAGMA journal_mode=WAL", "CREATE TABLE t (v)"]);
        using (var writer = Open(database))
        {
            Exec(writer, "PRAGMA wal_autocheckpoint=0");
            Exec(writer, "INSERT INTO t VALUES (1)");
            Exec(writer, "INSERT INTO t VALUES (2)");

            var snapshot = SqliteSnapshots.BuildSqliteSnapshot(database);

            snapshot.Tables.Should().ContainSingle().Which.RowCount.Should().Be(2);
        }
    }

    /// <summary>
    /// Python's read-write open rolls a hot journal back (rewriting the database) and exports the rolled-back content; the read-only open
    /// cannot, and SQLite refuses the database (plan decision 7).
    /// </summary>
    [Fact]
    public void AHotJournalIsRefusedRatherThanRolledBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "copying a database whose writer holds its locks");
        var source = Tmp("source.sqlite");
        SqlTestDatabase.Build(source, ["CREATE TABLE t (v)", "INSERT INTO t VALUES (1)"]);
        var copy = Directory.CreateDirectory(Tmp("copy")).FullName;
        using (var writer = Open(source))
        {
            Exec(writer, "PRAGMA cache_size=1");
            Exec(writer, "BEGIN");
            Exec(writer, "INSERT INTO t SELECT randomblob(5000) FROM (WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r WHERE x < 50) SELECT x FROM r)");
            File.Copy(source, Path.Combine(copy, "db.sqlite"));
            File.Copy(source + "-journal", Path.Combine(copy, "db.sqlite-journal"));
            Exec(writer, "ROLLBACK");
        }

        var before = File.ReadAllBytes(Path.Combine(copy, "db.sqlite"));
        var build = () => SqliteSnapshots.BuildSqliteSnapshot(Path.Combine(copy, "db.sqlite"));

        build.Should().Throw<Sqlite3Exception>()
            .Where(exc => exc.TypeName == "OperationalError" && exc.Message == "attempt to write a readonly database");
        File.ReadAllBytes(Path.Combine(copy, "db.sqlite")).Should().Equal(before);
    }

    private static string Describe(OrderedDictionary<string, IReadOnlyList<string>> map)
        => string.Join(';', map.Select(item => item.Key + "=" + string.Join(',', item.Value)));

    private static SqliteConnection Memory() => Open(":memory:");

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string Tmp(string name) => Path.Combine(_tmp.FullName, name);
}
