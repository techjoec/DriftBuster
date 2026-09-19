using System.Text.Json;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>SQLite exports: tables, masking, hashing, limits, storage classes, and the export manifest.</summary>
public sealed class SqlExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-export-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Database(params object[] steps)
    {
        var path = Path.Join(_tmp.FullName, $"db{Guid.NewGuid():N}.sqlite");
        SqlTestDatabase.Build(path, [.. steps]);
        return path;
    }

    [Fact]
    public void The_sample_database_exports_with_masked_and_hashed_columns()
    {
        var settings = new SqlExportSettings
        {
            MaskedColumns = SqlExportSettings.ParseColumns(["accounts.secret"]),
            HashedColumns = SqlExportSettings.ParseColumns([" accounts . email "]),
            HashSalt = "s",
        };

        var snapshot = SqliteSnapshots.Build(SqlTestDatabase.SampleFixture, settings, new FixedTimeProvider(Now));

        snapshot.Database.Should().Be("sample.sqlite");
        snapshot.CapturedAt.Should().Be(Now);
        var table = snapshot.Tables.Should().ContainSingle().Subject;
        table.Columns.Should().Equal("id", "email", "secret", "balance");
        table.RowCount.Should().Be(2);
        table.Rows[0]["secret"]!.GetValue<string>().Should().Be("[REDACTED]");
        table.Rows[0]["email"]!.GetValue<string>().Should().Be(SqliteSnapshots.HashValue(JsonValue.Create("alice@example.com"), "accounts.email:s"));
        table.Rows[0]["balance"]!.GetValue<double>().Should().Be(42.5);
        table.Rows[0]["id"]!.GetValue<long>().Should().Be(1);
        SqliteSnapshots.HashValue(JsonValue.Create("alice@example.com"), "accounts.email:s").Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public void Tables_filter_limit_and_every_storage_class_including_quoted_names()
    {
        var path = Database(
            "CREATE TABLE \"odd \"\"name\" (a, b)",
            SqlTestDatabase.Step("INSERT INTO \"odd \"\"name\" VALUES (?, ?)", new byte[] { 1, 2 }, null),
            SqlTestDatabase.Step("INSERT INTO \"odd \"\"name\" VALUES (?, ?)", "text", 7L),
            "CREATE TABLE skipped (x)");

        var snapshot = SqliteSnapshots.Build(path, new SqlExportSettings { ExcludeTables = ["skipped"], Limit = 1 });

        var table = snapshot.Tables.Should().ContainSingle().Subject;
        table.Name.Should().Be("odd \"name");
        table.RowCount.Should().Be(2);
        table.Rows.Should().ContainSingle();
        table.Rows[0].ToJsonString().Should().Be("""{"a":{"type":"base64","value":"AQI="},"b":null}""");
        FluentActions.Invoking(() => SqlExportSettings.ParseColumns(["nodot"])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Export_writes_snapshots_and_a_manifest_only_when_something_was_exported()
    {
        var output = Path.Join(_tmp.FullName, "out");
        var runner = new CaptureRunner(new FixedTimeProvider(Now));
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        runner.ExportSql(new SqlExportOptions { Databases = [Path.Join(_tmp.FullName, "missing.sqlite")], OutputDir = output }, stdout, stderr).Should().Be(1);
        stderr.ToString().Should().StartWith("error: database not found: ");
        Directory.Exists(output).Should().BeFalse();
        runner.ExportSql(new SqlExportOptions { Databases = [SqlTestDatabase.SampleFixture], Settings = new SqlExportSettings { Limit = 0 } }, stdout, stderr).Should().Be(1);

        var exitCode = runner.ExportSql(
            new SqlExportOptions { Databases = [SqlTestDatabase.SampleFixture, SqlTestDatabase.SampleFixture], OutputDir = output, Prefix = "p", ReportManifestPath = true },
            stdout,
            stderr);

        exitCode.Should().Be(0);
        var export = runner.LastExport!.Value;
        export.SnapshotPaths.Select(Path.GetFileName).Should().Equal("p-sample-sql-snapshot.json", "p-sample-sql-snapshot-1.json");
        export.Manifest.Exports.Should().HaveCount(2).And.OnlyContain(entry => entry.RowCounts["accounts"] == 2);
        var written = JsonSerializer.Deserialize(File.ReadAllText(export.ManifestPath), ModelJson.TypeInfo<SqlExportManifest>());
        written.Should().BeEquivalentTo(export.Manifest);
        JsonSerializer.Deserialize(File.ReadAllText(export.SnapshotPaths[0]), ModelJson.TypeInfo<SqlSnapshot>())!.Tables[0].RowCount.Should().Be(2);
        stdout.ToString().Should().Contain("Manifest written to " + export.ManifestPath);
    }
}
