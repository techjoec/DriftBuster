using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;
using DriftBuster.Backend.Tests.Infrastructure;

using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// SQL snapshot edges: the one-statement check, the written file text, and what the read-only open does to the database and its journal.
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

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("SELECT 1; -- trailing comment", true)]
    [InlineData("SELECT 1; SELECT 2", false)]
    [InlineData("SELECT 1; /* c */ x", false)]
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

    /// <summary>The read-only open cannot roll a hot journal back, so SQLite refuses the database and leaves it unchanged.</summary>
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

    private string Tmp(string name) => Path.Combine(_tmp.FullName, name);
}
