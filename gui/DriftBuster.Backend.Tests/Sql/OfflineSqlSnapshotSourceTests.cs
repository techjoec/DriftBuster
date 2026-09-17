using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// Mirror of the SQL tests of tests/offline/test_offline_runner_config_helpers.py over <see cref="OfflineSqlSnapshotSource"/>.
/// <c>test_offline_runner_profile_with_registry_and_sql_sources</c> reads an <c>OfflineRunnerProfile</c> holding a path, a
/// <c>registry_scan</c> and a <c>sql_snapshot</c> source; the port's reader (<see cref="RunProfile.FromOfflineRunnerDict"/>) refuses
/// those two source kinds by decision (registry scan and SQL export are their own commands), so the mirror asserts that refusal and
/// parses each structured entry with its own source type.
/// </summary>
public sealed class OfflineSqlSnapshotSourceTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-offline-sql-source-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Tmp(string name) => Path.Combine(_tmp.FullName, name);

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    private static List<object?> Items(params object?[] values) => [.. values];

    [Fact]
    public void NormaliseSnapshotColumnsHandlesSequences()
    {
        var mapping = OfflineSqlSnapshotSource.NormaliseSnapshotColumns(Map(
            ("users", Items("id", "email")),
            ("events", Items("timestamp", "severity"))));
        mapping["users"].Should().Equal("id", "email");
        mapping["events"].Should().Equal("timestamp", "severity");

        var sequence = OfflineSqlSnapshotSource.NormaliseSnapshotColumns(Items(
            "audit.id",
            "audit.created",
            "logs.message",
            "invalid",
            "logs."));
        sequence["audit"].Should().Equal("id", "created");
        sequence["logs"].Should().Equal("message");
        OfflineSqlSnapshotSource.NormaliseSnapshotColumns(null).Should().BeEmpty();
    }

    [Fact]
    public void OfflineSqlSnapshotSourceFromDictAndKwargs()
    {
        var payload = Map(
            ("sql_snapshot", Map(
                ("path", Tmp("db.sqlite")),
                ("tables", Items("users", "logs")),
                ("exclude_tables", "audit"),
                ("mask_columns", Map(("users", Items("password")))),
                ("hash_columns", Items("users.email", "users.id")),
                ("limit", "25"),
                ("placeholder", "[MASKED]"),
                ("hash_salt", "pepper"))),
            ("alias", " database "));
        var source = OfflineSqlSnapshotSource.FromDict(payload);
        source.Path.Should().Be(Tmp("db.sqlite"));
        source.Tables.Should().Equal("users", "logs");
        source.ExcludeTables.Should().Equal("audit");
        source.MaskColumns["users"].Should().Equal("password");
        source.HashColumns["users"].Should().Equal("email", "id");
        source.Limit.Should().Be(new System.Numerics.BigInteger(25));
        source.Placeholder.Should().Be("[MASKED]");
        source.HashSalt.Should().Be("pepper");
        source.DestinationName(fallbackIndex: 2).Should().Be("database");

        var kwargs = source.SnapshotKwargs();
        ((List<object?>)kwargs["tables"]!).Should().Equal("users", "logs");
        kwargs["limit"].Should().Be(25L);
    }

    // str(spec.get(key) or payload.get(key) or default): a falsy payload value falls through to the default (CPython: '' and 0 give
    // '[REDACTED]' and '', a spec placeholder 0 over a payload False gives '[REDACTED]').
    [Fact]
    public void OfflineSqlSnapshotSourceFalsyPayloadPlaceholderAndSaltFallThroughToTheDefaults()
    {
        var source = OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "x"))), ("placeholder", string.Empty), ("hash_salt", 0L)));
        source.Placeholder.Should().Be("[REDACTED]");
        source.HashSalt.Should().BeEmpty();

        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "x"), ("placeholder", 0L))), ("placeholder", false))).Placeholder.Should().Be("[REDACTED]");
        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "x"))), ("hash_salt", 7L))).HashSalt.Should().Be("7");
    }

    [Fact]
    public void OfflineSqlSnapshotSourceLimitValidation()
    {
        var payload = Map(("sql_snapshot", Map(("path", "sample.db"), ("limit", 0L))));
        var act = () => OfflineSqlSnapshotSource.FromDict(payload);
        act.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void OfflineSqlSnapshotSourceDialectValidation()
    {
        var payload = Map(("sql_snapshot", Map(("path", "sample.db"), ("dialect", "postgres"))));
        var act = () => OfflineSqlSnapshotSource.FromDict(payload);
        act.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void OfflineRunnerProfileWithRegistryAndSqlSources()
    {
        var filePath = Tmp("config.txt");
        File.WriteAllText(filePath, "example");
        var registryEntry = Map(("registry_scan", Map(
            ("token", "ExampleToken"),
            ("keywords", Items("alpha")),
            ("patterns", Items("value")))));
        var sqlEntry = Map(("sql_snapshot", Map(("path", "sample.db"))));
        var payload = Map(
            ("name", "profile-sample"),
            ("sources", Items(Map(("path", filePath)), registryEntry, sqlEntry)),
            ("baseline", filePath),
            ("tags", Items("audit")),
            ("options", Map(("secret_ignore_rules", Items("PasswordAssignment")))),
            ("secret_scanner", Map(("ignore_rules", Items("GenericApiToken")))));

        var read = () => RunProfile.FromOfflineRunnerDict(payload);
        read.Should().Throw<PythonValueException>().WithMessage("Run profiles do not support 'registry_scan' sources.*");

        OfflineRegistryScanSource.FromDict(registryEntry).Token.Should().Be("ExampleToken");
        OfflineSqlSnapshotSource.FromDict(sqlEntry).Path.Should().Be("sample.db");
    }

    [Fact]
    public void DestinationNameFallsBackToTheStemThenTheIndex()
    {
        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "/var/lib/app.data.sqlite"))))).DestinationName(3).Should().Be("app-data");
        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", ".env"))))).DestinationName(3).Should().Be("-env");
        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "/"))))).DestinationName(3).Should().Be("sql_snapshot_03");
        OfflineSqlSnapshotSource.FromDict(Map(("sql_snapshot", Map(("path", "x"))), ("alias", "a b"))).DestinationName(3).Should().Be("a-b");
    }
}
