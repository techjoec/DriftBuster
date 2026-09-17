using DriftBuster.Backend.Sql;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// <c>sqlite3.connect(":memory:")</c> opens a new empty in-memory database, so a file of that name (Linux only: Windows file names cannot
/// hold ":") exports no tables once the existence check has passed, and a directory of that name is not refused either; an absolute path
/// to such a file is a real file. The spelling compared is <c>str(Path(path))</c>, so the test changes the process working directory and
/// runs alone.
/// </summary>
[Collection(ProcessWideSeamCollection.Name)]
public sealed class SqliteInMemoryNameTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-memory-name-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void AFileNamedMemoryExportsAnEmptyInMemoryDatabase()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold ':'");
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tmp.FullName);
        try
        {
            SqlTestDatabase.Build("real.sqlite", ["CREATE TABLE t (v)", "INSERT INTO t VALUES (1)"]);
            File.Copy("real.sqlite", ":memory:");
            var relative = SqliteSnapshots.BuildSqliteSnapshot(":memory:");
            relative.Tables.Should().BeEmpty();
            relative.Path.Should().Be(":memory:");
            relative.Database.Should().Be(":memory:");
            SqliteSnapshots.BuildSqliteSnapshot("./:memory:").Tables.Should().BeEmpty();
            SqliteSnapshots.BuildSqliteSnapshot(Path.Combine(_tmp.FullName, ":memory:")).Tables.Should().ContainSingle().Which.Name.Should().Be("t");

            Directory.SetCurrentDirectory(Directory.CreateDirectory("dir").FullName);
            var missing = () => SqliteSnapshots.BuildSqliteSnapshot(":memory:");
            missing.Should().Throw<FileNotFoundException>().WithMessage("Database not found: :memory:");
            Directory.CreateDirectory(":memory:");
            SqliteSnapshots.BuildSqliteSnapshot(":memory:").Tables.Should().BeEmpty();
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}
