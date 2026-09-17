using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// <c>sqlite_master</c> rows a writable schema stored as BLOBs. Python keeps a BLOB <c>sql</c> as <c>bytes</c> in the snapshot, so only
/// <c>json.dumps</c> refuses it once every table is built, and <c>_iter_tables</c> is a generator, so a BLOB name raises only when the
/// export reaches it. Each expectation is CPython 3.13's outcome for the same database.
/// </summary>
public sealed class SqlBlobSchemaTests : IDisposable
{
    private const string BytesNotSerializable = "Object of type bytes is not JSON serializable";
    private const string StartsWithTypeError = "startswith first arg must be bytes or a tuple of bytes, not str";

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-blob-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void ABlobSchemaBuildsAndOnlyTheJsonDumpRefusesIt()
    {
        var database = Database("blob.sqlite", "CREATE TABLE a (v)", "INSERT INTO a VALUES (1)", "PRAGMA writable_schema=ON", BlobSchema("a"));

        var snapshot = SqliteSnapshots.BuildSqliteSnapshot(database);
        snapshot.Tables.Should().ContainSingle();
        snapshot.Tables[0].Schema.Should().BeNull();
        snapshot.Tables[0].SchemaBytes.Should().Equal("CREATE TABLE a (v)"u8.ToArray());
        snapshot.Tables[0].RowCount.Should().Be(1);

        var destination = Path.Combine(_tmp.FullName, "out.json");
        var write = () => SqliteSnapshots.WriteSqliteSnapshot(database, destination);
        write.Should().Throw<PythonTypeException>().WithMessage(BytesNotSerializable);
        File.Exists(destination).Should().BeFalse("json.dumps raises before write_text opens the destination");
    }

    [Fact]
    public void AnErrorInALaterTableWinsOverABlobSchema()
    {
        var database = Database(
            "later.sqlite",
            "CREATE TABLE a (v)",
            "CREATE TABLE b (w)",
            "INSERT INTO b VALUES (CAST(x'ff' AS TEXT))",
            "PRAGMA writable_schema=ON",
            BlobSchema("a"));

        var write = () => SqliteSnapshots.WriteSqliteSnapshot(database, Path.Combine(_tmp.FullName, "out.json"));
        write.Should().Throw<Sqlite3Exception>().WithMessage("Could not decode to UTF-8 column 'w' with text '\uFFFD'");
        var nan = () => SqliteSnapshots.WriteSqliteSnapshot(database, Path.Combine(_tmp.FullName, "out.json"), limit: double.NaN);
        nan.Should().Throw<PythonValueException>().WithMessage("cannot convert float NaN to integer");
    }

    [Fact]
    public void TablesBeforeABlobNameAreExportedFirst()
    {
        string[] blobName = ["CREATE TABLE z (w)", "PRAGMA writable_schema=ON", "UPDATE sqlite_master SET name=CAST(name AS BLOB), tbl_name=CAST(tbl_name AS BLOB) WHERE name='z'"];
        var plain = Database("plain.sqlite", ["CREATE TABLE a (v)", "INSERT INTO a VALUES (1)", .. blobName]);
        var build = () => SqliteSnapshots.BuildSqliteSnapshot(plain);
        build.Should().Throw<PythonTypeException>().WithMessage(StartsWithTypeError);
        var nan = () => SqliteSnapshots.BuildSqliteSnapshot(plain, limit: double.NaN);
        nan.Should().Throw<PythonValueException>().WithMessage("cannot convert float NaN to integer", "table a is exported before the BLOB row is reached");

        var decode = Database("decode.sqlite", ["CREATE TABLE a (v)", "INSERT INTO a VALUES (CAST(x'ff' AS TEXT))", .. blobName]);
        var decodeBuild = () => SqliteSnapshots.BuildSqliteSnapshot(decode);
        decodeBuild.Should().Throw<Sqlite3Exception>().WithMessage("Could not decode to UTF-8 column 'v' with text '\uFFFD'");
    }

    [Fact]
    public void CaptureExportSqlReportsEarlierFailuresAndStopsAtTheBlobSchemaDump()
    {
        var plain = Database("plain.sqlite", "CREATE TABLE p (v)", "INSERT INTO p VALUES (1)");
        var failing = Database(
            "failing.sqlite", "CREATE TABLE a (v)", "CREATE TABLE b (w)", "INSERT INTO b VALUES (CAST(x'ff' AS TEXT))", "PRAGMA writable_schema=ON", BlobSchema("a"));
        var blob = Database("blob.sqlite", "CREATE TABLE a (v)", "PRAGMA writable_schema=ON", BlobSchema("a"));
        var output = Path.Combine(_tmp.FullName, "sql");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var outcome = CaptureRunner.RunSqlExport(new SqlExportOptions { Database = [plain, failing], OutputDir = output }, stdout, stderr);
        outcome.ExitCode.Should().Be(1);
        stderr.ToString().Should().Be($"error: failed to export {failing}: Could not decode to UTF-8 column 'w' with text '\uFFFD'\n");
        File.Exists(Path.Combine(output, "sql-manifest.json")).Should().BeTrue();

        var blobOutput = Path.Combine(_tmp.FullName, "sql2");
        var export = () => CaptureRunner.RunSqlExport(new SqlExportOptions { Database = [plain, blob], OutputDir = blobOutput }, TextWriter.Null, TextWriter.Null);
        export.Should().Throw<PythonTypeException>().WithMessage(BytesNotSerializable);
        Directory.GetFiles(blobOutput).Select(Path.GetFileName).Should().Equal("plain-sql-snapshot.json");
    }

    [Fact]
    public void TheOfflineCollectorRaisesTheDumpErrorWithoutWriting()
    {
        var blob = Database("blob.sqlite", "CREATE TABLE a (v)", "PRAGMA writable_schema=ON", BlobSchema("a"));
        var destination = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "dest")).FullName;
        var source = OfflineSqlSnapshotSource.FromDict(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sql_snapshot"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = blob },
        });
        var collect = () => SqlSnapshotCollector.Collect(source, destination, "db");
        collect.Should().Throw<PythonTypeException>().WithMessage(BytesNotSerializable);
        Directory.EnumerateFileSystemEntries(destination).Should().BeEmpty();
    }

    private static string BlobSchema(string table) => $"UPDATE sqlite_master SET sql = CAST(sql AS BLOB) WHERE name = '{table}'";

    private string Database(string name, params string[] statements)
    {
        var path = Path.Combine(_tmp.FullName, name);
        SqlTestDatabase.Build(path, statements.Select(object? (sql) => SqlTestDatabase.Step(sql)));
        return path;
    }
}
