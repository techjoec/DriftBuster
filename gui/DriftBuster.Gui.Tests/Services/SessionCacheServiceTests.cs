using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
        SessionCacheMigrationCounters.Reset();

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

    [Fact]
    public async Task LoadAsync_waits_for_migration_and_reads_legacy_cache()
    {
        SessionCacheMigrationCounters.Reset();

        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "legacy", "multi-server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);

        var legacySnapshot = new ServerSelectionCache
        {
            PersistSession = true,
            ActivityFilter = "legacy-filter",
            Servers =
            {
                new ServerSelectionCacheEntry
                {
                    HostId = "legacy",
                    Label = "Legacy Host",
                    Enabled = true,
                },
            },
        };

        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacySnapshot, SerializerOptions));

        var migrationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task ControlledMigration(string legacy, string destination, CancellationToken token)
            => ControlledMigrationAsync(legacy, destination, migrationStarted, migrationRelease, token);

        var service = new SessionCacheService(temp.Path, legacyPath, ControlledMigration);

        var loadTask = service.LoadAsync();

        await migrationStarted.Task;
        loadTask.IsCompleted.Should().BeFalse();

        migrationRelease.TrySetResult(true);

        var loaded = await loadTask;

        loaded.Should().NotBeNull();
        loaded!.ActivityFilter.Should().Be("legacy-filter");
        SessionCacheMigrationCounters.Successes.Should().Be(1);
        SessionCacheMigrationCounters.Failures.Should().Be(0);

        File.Exists(Path.Combine(temp.Path, "multi-server.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_load_and_save_share_single_migration()
    {
        SessionCacheMigrationCounters.Reset();

        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "legacy", "multi-server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);

        var legacySnapshot = new ServerSelectionCache
        {
            PersistSession = true,
        };

        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacySnapshot, SerializerOptions));

        var migrationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        Task ControlledMigration(string legacy, string destination, CancellationToken token)
        {
            Interlocked.Increment(ref invocationCount);
            return ControlledMigrationAsync(legacy, destination, migrationStarted, migrationRelease, token);
        }

        var service = new SessionCacheService(temp.Path, legacyPath, ControlledMigration);

        var loadTask = service.LoadAsync();
        var saveSnapshot = new ServerSelectionCache
        {
            PersistSession = false,
            ActivityFilter = "after-upgrade",
        };
        var saveTask = service.SaveAsync(saveSnapshot);

        await migrationStarted.Task;
        loadTask.IsCompleted.Should().BeFalse();
        saveTask.IsCompleted.Should().BeFalse();

        migrationRelease.TrySetResult(true);

        await Task.WhenAll(loadTask, saveTask);

        invocationCount.Should().Be(1);
        SessionCacheMigrationCounters.Successes.Should().Be(1);
        SessionCacheMigrationCounters.Failures.Should().Be(0);

        var reloaded = await service.LoadAsync();
        reloaded.Should().NotBeNull();
        reloaded!.ActivityFilter.Should().Be("after-upgrade");
    }

    [Fact]
    public async Task Migration_failure_is_counted_and_operations_continue()
    {
        SessionCacheMigrationCounters.Reset();

        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "legacy", "multi-server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "{\"schema_version\":2}");

        Task ControlledMigration(string _, string __, CancellationToken ___)
        {
            SessionCacheMigrationCounters.RecordFailure();
            return Task.CompletedTask;
        }

        var service = new SessionCacheService(temp.Path, legacyPath, ControlledMigration);

        (await service.LoadAsync()).Should().BeNull();
        SessionCacheMigrationCounters.Successes.Should().Be(0);
        SessionCacheMigrationCounters.Failures.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_trigger_lock_violations()
    {
        SessionCacheMigrationCounters.Reset();

        using var temp = new TempDirectory();
        var serviceA = new SessionCacheService(temp.Path);
        var serviceB = new SessionCacheService(temp.Path);

        var saveTasks = Enumerable.Range(0, 10).Select(i =>
        {
            var service = i % 2 == 0 ? serviceA : serviceB;
            var snapshot = new ServerSelectionCache
            {
                PersistSession = i % 3 == 0,
                ActivityFilter = $"filter-{i}",
            };

            return service.SaveAsync(snapshot);
        }).ToArray();

        await Task.WhenAll(saveTasks);

        var loaded = await serviceA.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.ActivityFilter.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_save_and_load_operations_are_serialised()
    {
        SessionCacheMigrationCounters.Reset();

        using var temp = new TempDirectory();
        var service = new SessionCacheService(temp.Path);

        await service.SaveAsync(new ServerSelectionCache
        {
            PersistSession = true,
            ActivityFilter = "seed",
        });

        var tasks = Enumerable.Range(0, 5).SelectMany(i => new Task[]
        {
            service.SaveAsync(new ServerSelectionCache
            {
                PersistSession = i % 2 == 0,
                ActivityFilter = $"value-{i}",
            }),
            Task.Run(async () =>
            {
                var loaded = await service.LoadAsync();
                loaded.Should().NotBeNull();
            }),
        }).ToArray();

        await Task.WhenAll(tasks);

        var finalSnapshot = await service.LoadAsync();
        finalSnapshot.Should().NotBeNull();
        finalSnapshot!.ActivityFilter.Should().NotBeNull();
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

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task ControlledMigrationAsync(
        string legacy,
        string destination,
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        started.TrySetResult(true);
        await release.Task.ConfigureAwait(false);

        await using var source = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        SessionCacheMigrationCounters.RecordSuccess();
    }
}
