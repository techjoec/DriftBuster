using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Backend.Tests.Curation;

namespace DriftBuster.Backend.Tests.History;

/// <summary>Scan history: runs recorded, file versions shared between runs, masked values kept as fingerprints, and the queries.</summary>
public sealed class HistoryStoreTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-history-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private HistoryStore Store() => new(Path.Combine(_tmp.FullName, "history.db"));

    private static readonly DateTimeOffset First = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_store_answers_with_nothing()
    {
        var store = Store();

        store.Stats().Should().Be(new HistoryStats(0, 0, 0));
        store.SettingHistory("a.json", "k").Should().BeEmpty();
        store.WhereSettingIsSet("k").Should().BeEmpty();
    }

    [Fact]
    public void Unchanged_files_share_their_version_between_runs()
    {
        var store = Store();
        var comparison = CurationSample.Build();

        store.Record(comparison, "hosts-1", First);
        var afterOne = store.Stats();
        store.Record(comparison, "hosts-1", First.AddDays(1));
        var afterTwo = store.Stats();

        afterOne.Runs.Should().Be(1);
        afterTwo.Runs.Should().Be(2);
        afterTwo.FileVersions.Should().Be(afterOne.FileVersions, "nothing changed between the runs");
        afterOne.FileVersions.Should().Be(4, "app.json has three distinct copies and legacy.json one");
        afterTwo.Bytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Setting_history_lists_every_run_and_server_newest_first()
    {
        var store = Store();
        store.Record(CurationSample.Build(), "hosts-1", First);
        store.Record(CurationSample.Build(), "hosts-1", First.AddDays(1));

        var history = store.SettingHistory("APPS/web/app.json", "Cache.Minutes");

        history.Should().HaveCount(6);
        history[0].RecordedAt.Should().Be(First.AddDays(1));
        history.Where(entry => string.Equals(entry.HostLabel, "prod", StringComparison.Ordinal)).Should().OnlyContain(entry => entry.Value == "60");
    }

    [Fact]
    public void Masked_values_are_kept_as_fingerprints_only()
    {
        var store = Store();
        store.Record(CurationSample.Build(), "hosts-1", First);

        var password = store.SettingHistory("apps/web/app.json", "db.password");

        password.Should().OnlyContain(entry => entry.Masked && entry.Value == null);
        password.Single(entry => string.Equals(entry.HostLabel, "prod", StringComparison.Ordinal)).ValueHash.Should().Be(CurationTarget.ValueHashOf("two"));
    }

    [Fact]
    public void Where_a_setting_is_set_and_where_a_value_appears_use_each_servers_latest_copy()
    {
        var store = Store();
        store.Record(CurationSample.Build(), "hosts-1", First);
        store.Record(CurationSample.Build(), "hosts-1", First.AddDays(1));

        var set = store.WhereSettingIsSet("k");
        set.Select(entry => entry.HostLabel).Should().Equal("baseline", "staging");
        set.Should().OnlyContain(entry => entry.RecordedAt == First.AddDays(1));

        var fifteen = store.WhereValueAppears(CurationTarget.ValueHashOf("15"));
        fifteen.Select(entry => (entry.Path, entry.Key, entry.HostLabel)).Should().Equal(
            ("apps/web/app.json", "Cache.Minutes", "baseline"),
            ("apps/web/app.json", "Cache.Minutes", "staging"));
    }

    [Fact]
    public void Clear_removes_every_run()
    {
        var store = Store();
        store.Record(CurationSample.Build(), "hosts-1", First);

        store.Clear();

        store.Stats().Runs.Should().Be(0);
        store.WhereSettingIsSet("k").Should().BeEmpty();
    }
}
