using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// SQL snapshots over <see cref="SqlTestDatabase.CreateSampleDatabase"/>: masking and hashing, limits, the capture SQL export manifest,
/// and the offline collector's <c>sql_snapshot</c> source (<see cref="SqlSnapshotCollector.Collect"/>).
/// </summary>
public sealed class SqlSnapshotsTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-snapshots-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void BuildSqliteSnapshotMasksAndHashes()
    {
        var dbPath = SqlTestDatabase.CreateSampleDatabase(Tmp("sample.sqlite"));

        var snapshot = SqliteSnapshots.BuildSqliteSnapshot(
            dbPath,
            maskColumns: Map(("accounts", new List<object?> { "secret" })),
            hashColumns: Map(("accounts", new List<object?> { "email" })),
            placeholder: "[MASK]",
            hashSalt: "pepper");

        var payload = snapshot.ToDict();
        payload["database"].Should().Be("sample.sqlite");
        payload["dialect"].Should().Be("sqlite");
        ((List<object?>)payload["tables"]!).Should().NotBeEmpty("expected exported tables");

        var accounts = (OrderedDictionary<string, object?>)((List<object?>)payload["tables"]!)[0]!;
        accounts["name"].Should().Be("accounts");
        accounts["row_count"].Should().Be(2L);
        ((List<object?>)accounts["masked_columns"]!).Should().Equal("secret");
        ((List<object?>)accounts["hashed_columns"]!).Should().Equal("email");

        var rows = ((List<object?>)accounts["rows"]!).Cast<OrderedDictionary<string, object?>>().ToList();
        rows[0]["secret"].Should().Be("[MASK]");
        ((string)rows[0]["email"]!).Should().StartWith("sha256:");
        EngineBuiltins.Float(rows[0]["balance"]).Should().BeApproximately(42.5, 42.5e-6);
    }

    [Fact]
    public void WriteSqliteSnapshotWithLimitsAndSequences()
    {
        var dbPath = SqlTestDatabase.CreateSampleDatabase(Tmp("limited.sqlite"));
        SqlTestDatabase.Build(
            dbPath,
            [
                "CREATE TABLE audit (id INTEGER PRIMARY KEY, payload BLOB)",
                "BEGIN",
                SqlTestDatabase.Step("INSERT INTO audit (payload) VALUES (?)", "audit"u8.ToArray()),
                "COMMIT",
            ]);

        var destination = Tmp("out.json");
        SqliteSnapshots.WriteSqliteSnapshot(
            dbPath,
            destination,
            tables: ["accounts"],
            excludeTables: ["nonexistent"],
            maskColumns: new[] { "accounts.secret" },
            hashColumns: new[] { "accounts.email" },
            limit: 1);

        EngineJson.TryLoads(File.ReadAllText(destination), out var loaded).Should().BeTrue();
        var table = (OrderedDictionary<string, object?>)((List<object?>)((OrderedDictionary<string, object?>)loaded!)["tables"]!)[0]!;
        EngineBuiltins.Int(table["row_count"]).Should().Be(2);
        ((List<object?>)table["rows"]!).Should().HaveCount(1);
        ((OrderedDictionary<string, object?>)((List<object?>)table["rows"]!)[0]!)["secret"].Should().Be("[REDACTED]");

        var auditSnapshot = SqliteSnapshots.BuildSqliteSnapshot(dbPath, tables: ["audit"]);
        var auditTable = (OrderedDictionary<string, object?>)((List<object?>)auditSnapshot.ToDict()["tables"]!)[0]!;
        var auditPayload = (OrderedDictionary<string, object?>)((OrderedDictionary<string, object?>)((List<object?>)auditTable["rows"]!)[0]!)["payload"]!;
        auditPayload["type"].Should().Be("base64");

        var build = () => SqliteSnapshots.BuildSqliteSnapshot(dbPath, limit: 0);
        build.Should().Throw<EngineValueException>();
    }

    [Fact]
    public void CaptureExportSqlSubcommandWritesManifest()
    {
        var dbPath = SqlTestDatabase.CreateSampleDatabase(Tmp("capture.sqlite"));
        var outputDir = Tmp("exports");

        var args = new SqlExportOptions
        {
            Database = [dbPath],
            OutputDir = outputDir,
            Table = [],
            ExcludeTable = [],
            MaskColumn = ["accounts.secret"],
            HashColumn = ["accounts.email"],
            Placeholder = "[MASK]",
            HashSalt = "pepper",
            Limit = null,
            Prefix = "demo",
        };

        var result = CaptureRunner.RunSqlExport(args, TextWriter.Null, TextWriter.Null);
        result.ExitCode.Should().Be(0);

        var manifestPath = Path.Combine(outputDir, "sql-manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        EngineJson.TryLoads(File.ReadAllText(manifestPath), out var loaded).Should().BeTrue();
        var manifest = (OrderedDictionary<string, object?>)loaded!;
        ((List<object?>)manifest["exports"]!).Should().NotBeEmpty();
        var exportEntry = (OrderedDictionary<string, object?>)((List<object?>)manifest["exports"]!)[0]!;
        ((List<object?>)exportEntry["tables"]!).Should().Equal("accounts");
        exportEntry["dialect"].Should().Be("sqlite");
        Canonicaliser.Dumps(exportEntry["masked_columns"], indent: false, ensureAscii: true, sortKeys: false).Should().Be("""{"accounts": ["secret"]}""");
        Canonicaliser.Dumps(exportEntry["hashed_columns"], indent: false, ensureAscii: true, sortKeys: false).Should().Be("""{"accounts": ["email"]}""");
        ((OrderedDictionary<string, object?>)manifest["options"]!)["hash_salt"].Should().Be("pepper");

        var snapshotPath = Path.Combine(outputDir, "demo-sql-snapshot.json");
        File.Exists(snapshotPath).Should().BeTrue();
        EngineJson.TryLoads(File.ReadAllText(snapshotPath), out var snapshotPayload).Should().BeTrue();
        var table = (OrderedDictionary<string, object?>)((List<object?>)((OrderedDictionary<string, object?>)snapshotPayload!)["tables"]!)[0]!;
        ((List<object?>)table["masked_columns"]!).Should().Equal("secret");
    }

    [Fact]
    public void OfflineRunnerSqlSnapshotSource()
    {
        var dbPath = SqlTestDatabase.CreateSampleDatabase(Tmp("runner.sqlite"));
        var sourcePayload = Map(
            ("sql_snapshot", Map(
                ("path", dbPath),
                ("mask_columns", Map(("accounts", new List<object?> { "secret" }))),
                ("hash_columns", Map(("accounts", new List<object?> { "email" }))),
                ("placeholder", "[MASK]"),
                ("hash_salt", "pepper"))),
            ("alias", "accounts-db"));
        var source = OfflineSqlSnapshotSource.FromDict(sourcePayload);
        var alias = source.DestinationName(fallbackIndex: 0);
        var destination = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "staging", "data", alias)).FullName;

        var result = SqlSnapshotCollector.Collect(source, destination, alias, baseDir: _tmp.FullName);

        result.ResultPath.Should().NotBeNull("expected collected files");
        EngineJson.TryLoads(File.ReadAllText(result.ResultPath!), out var exported).Should().BeTrue();
        var accountTable = (OrderedDictionary<string, object?>)((List<object?>)((OrderedDictionary<string, object?>)exported!)["tables"]!)[0]!;
        ((List<object?>)accountTable["masked_columns"]!).Should().Equal("secret");
        ((string)((OrderedDictionary<string, object?>)((List<object?>)accountTable["rows"]!)[0]!)["email"]!).Should().StartWith("sha256:");

        var summary = result.Summary;
        summary["type"].Should().Be("sql_snapshot");
        summary["alias"].Should().Be("accounts-db");
        ((List<object?>)summary["tables"]!).Should().Equal("accounts");
        var metadataEntry = result.Metadata.Should().NotBeNull("expected sql metadata entries in manifest").And.Subject;
        metadataEntry["alias"].Should().Be("accounts-db");
        Canonicaliser.Dumps(metadataEntry["masked_columns"], indent: false, ensureAscii: true, sortKeys: false).Should().Be("""{"accounts": ["secret"]}""");
        Canonicaliser.Dumps(metadataEntry["hashed_columns"], indent: false, ensureAscii: true, sortKeys: false).Should().Be("""{"accounts": ["email"]}""");
        metadataEntry["placeholder"].Should().Be("[MASK]");
    }

    [Fact]
    public void OfflineRunnerSqlSnapshotOptional()
    {
        var missing = Tmp("missing.sqlite");
        var source = OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", missing), ("optional", true)))));
        var alias = source.DestinationName(fallbackIndex: 0);
        var destination = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "optional", "data", alias)).FullName;

        var result = SqlSnapshotCollector.Collect(source, destination, alias, baseDir: _tmp.FullName);

        result.ResultPath.Should().BeNull();
        Directory.EnumerateFiles(destination).Should().BeEmpty();
        var summary = result.Summary;
        summary["type"].Should().Be("sql_snapshot");
        summary["skipped"].Should().Be(true);
        summary["reason"].Should().Be("missing");
    }

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    private string Tmp(string name) => Path.Combine(_tmp.FullName, name);
}
