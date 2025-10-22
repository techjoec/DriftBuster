using System;
using System.IO;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Services;

public sealed class SessionCacheServiceTests
{
    [Fact]
    public async Task Save_load_and_clear_roundtrip()
    {
        using var temp = new TempDirectory();
        var service = new SessionCacheService(temp.Path);

        var snapshot = new ServerSelectionCache
        {
            PersistSession = true,
            Servers =
            {
                new ServerSelectionCacheEntry
                {
                    HostId = "server01",
                    Label = "Primary",
                    Enabled = true,
                    Scope = ServerScanScope.CustomRoots,
                    Roots = new[] { "C:/Configs" },
                },
            },
            Activities =
            {
                new ActivityCacheEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Severity = "Info",
                    Summary = "Ran scan",
                    Detail = "Evaluated 4 configs",
                    Category = "General",
                },
            },
        };

        await service.SaveAsync(snapshot);
        var loaded = await service.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.PersistSession.Should().BeTrue();
        loaded.Servers.Should().HaveCount(1);

        service.Clear();
        (await service.LoadAsync()).Should().BeNull();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
