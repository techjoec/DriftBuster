using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

/// <summary>The Multi-server session file: round trip, clearing, and strict reads that name the file and JSON path.</summary>
public sealed class SessionCacheServiceTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-session-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string CachePath => Path.Join(_tmp.FullName, SessionCacheService.FileName);

    [Fact]
    public async Task A_session_is_saved_loaded_and_cleared()
    {
        var service = new SessionCacheService(_tmp.FullName);
        var snapshot = new ServerSelectionCache
        {
            PersistSession = true,
            Servers = [new ServerSelectionCacheEntry { HostId = "server01", Label = "Primary", Enabled = true, Scope = ServerScanScope.CustomRoots, Roots = ["C:/Configs"] }],
            Activities = [new ActivityCacheEntry { Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Severity = "Info", Summary = "Ran scan", Category = "General" }],
            CatalogSort = new CatalogSortCache { Column = "drift", Descending = true },
            Timeline = new ActivityTimelineCache { Filter = "All" },
        };

        await service.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        service.CachePath.Should().Be(CachePath);
        File.ReadAllText(CachePath).Should().Contain("\"scope\": \"custom_roots\"").And.Contain("\"host_id\": \"server01\"");
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        loaded.Should().BeEquivalentTo(snapshot);

        service.Clear();
        service.Clear();
        (await service.LoadAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("{ broken", "$")]
    [InlineData("""{"schema_version": 3, "persist_session": true, "extra": 1}""", "$")]
    [InlineData("""{"schema_version": 3, "servers": [{"host_id": "a"}]}""", "$.servers[0]")]
    [InlineData("""{"schema_version": 2}""", "$.schema_version")]
    [InlineData("null", "null")]
    public void A_file_that_cannot_be_read_is_refused_and_left_alone(string text, string where)
    {
        File.WriteAllText(CachePath, text);
        var service = new SessionCacheService(_tmp.FullName);

        var refused = FluentActions.Invoking(service.Read).Should().Throw<InvalidDataException>().Which;

        refused.Message.Should().StartWith(CachePath + ": ").And.Contain(where);
        File.ReadAllText(CachePath).Should().Be(text);
    }
}
