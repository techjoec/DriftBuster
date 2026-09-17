using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// <see cref="OfflineSqlSnapshotSource"/>: column maps, payload parsing, validation and destination names.
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

    [Fact]
    public void OfflineSqlSnapshotSourceLimitValidation()
    {
        var payload = Map(("sql_snapshot", Map(("path", "sample.db"), ("limit", 0L))));
        var act = () => OfflineSqlSnapshotSource.FromDict(payload);
        act.Should().Throw<EngineValueException>();
    }

    [Fact]
    public void OfflineSqlSnapshotSourceDialectValidation()
    {
        var payload = Map(("sql_snapshot", Map(("path", "sample.db"), ("dialect", "postgres"))));
        var act = () => OfflineSqlSnapshotSource.FromDict(payload);
        act.Should().Throw<EngineValueException>();
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
