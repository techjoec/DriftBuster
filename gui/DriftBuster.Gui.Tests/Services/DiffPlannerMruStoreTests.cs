using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

/// <summary>The Diff planner's recent file sets: round trip, recording, limits, clearing and strict reads.</summary>
public sealed class DiffPlannerMruStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-mru-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string StorePath => Path.Join(_tmp.FullName, "diff-planner", DiffPlannerMruStore.FileName);

    private static DiffPlannerMruEntry Entry(string baseline, params string[] comparisons) =>
        new() { BaselinePath = baseline, ComparisonPaths = comparisons, LastUsedUtc = Now };

    [Fact]
    public async Task Entries_are_saved_trimmed_and_loaded()
    {
        var store = new DiffPlannerMruStore(_tmp.FullName);
        var entry = Entry(" /configs/baseline.json ", "/configs/a.json", "", "/CONFIGS/A.json", "/configs/b.json") with
        {
            DisplayName = " Critical diff ",
            PayloadKind = DiffPlannerPayloadKind.Sanitized,
            SanitizedDigest = "sha256:abc123",
        };

        await store.SaveAsync(new DiffPlannerMruSnapshot { Entries = [entry, Entry(" ", "/x"), Entry("/y")] }, TestContext.Current.CancellationToken);

        store.StorePath.Should().Be(StorePath);
        File.ReadAllText(StorePath).Should().Contain("\"payload_kind\": \"sanitized\"");
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        loaded.SchemaVersion.Should().Be(DiffPlannerMruStore.CurrentSchemaVersion);
        loaded.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(entry with
        {
            BaselinePath = "/configs/baseline.json",
            ComparisonPaths = ["/configs/a.json", "/configs/b.json"],
            DisplayName = "Critical diff",
        });
    }

    [Fact]
    public async Task Recording_puts_the_entry_first_replaces_the_same_files_and_keeps_the_limit()
    {
        var store = new DiffPlannerMruStore(_tmp.FullName);
        for (var index = 0; index < DiffPlannerMruStore.DefaultEntryLimit + 2; index++)
        {
            await store.RecordAsync(Entry($"/b{index}.json", "/c.json") with { LastUsedUtc = Now.AddMinutes(index) }, TestContext.Current.CancellationToken);
        }

        await store.RecordAsync(Entry("/B5.JSON", "/C.json") with { LastUsedUtc = Now.AddHours(1), DisplayName = "again" }, TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("/ignored.json"), TestContext.Current.CancellationToken);

        var entries = (await store.LoadAsync(TestContext.Current.CancellationToken)).Entries;
        entries.Should().HaveCount(DiffPlannerMruStore.DefaultEntryLimit);
        entries[0].DisplayName.Should().Be("again");
        entries.Count(entry => DiffPlannerMruStore.SameFiles(entry, Entry("/b5.json", "/c.json"))).Should().Be(1);
        entries.Select(entry => entry.BaselinePath).Should().NotContain(["/b0.json", "/b1.json"]);
    }

    [Fact]
    public async Task Clearing_removes_the_file()
    {
        var store = new DiffPlannerMruStore(_tmp.FullName);
        await store.ClearAsync(TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("/a.json", "/b.json"), TestContext.Current.CancellationToken);
        File.Exists(StorePath).Should().BeTrue();

        await store.ClearAsync(TestContext.Current.CancellationToken);

        File.Exists(StorePath).Should().BeFalse();
        (await store.LoadAsync(TestContext.Current.CancellationToken)).Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{ broken", "$")]
    [InlineData("""{"schema_version": 3, "max_entries": 4}""", "$")]
    [InlineData("""{"schema_version": 3, "entries": [{"comparison_paths": ["/b"]}]}""", "$.entries[0]")]
    [InlineData("""{"schema_version": 2}""", "$.schema_version")]
    public async Task A_file_that_cannot_be_read_is_refused_and_left_alone(string text, string where)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, text);
        var store = new DiffPlannerMruStore(_tmp.FullName);

        var refused = (await FluentActions.Awaiting(() => store.RecordAsync(Entry("/a.json", "/b.json"), TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidDataException>()).Which;

        refused.Message.Should().StartWith(StorePath + ": ").And.Contain(where);
        File.ReadAllText(StorePath).Should().Be(text);
    }
}
