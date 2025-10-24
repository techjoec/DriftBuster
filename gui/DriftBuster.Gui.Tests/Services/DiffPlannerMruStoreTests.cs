using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Services;

public sealed class DiffPlannerMruStoreTests
{
    [Fact]
    public async Task Save_and_load_roundtrip()
    {
        using var temp = new TempDirectory();
        var store = new DiffPlannerMruStore(temp.Path);

        var snapshot = new DiffPlannerMruSnapshot
        {
            MaxEntries = 8,
            Entries =
            {
                new DiffPlannerMruEntry
                {
                    BaselinePath = "/configs/baseline.json",
                    ComparisonPaths =
                    {
                        "/configs/compare-a.json",
                        "/configs/compare-b.json",
                    },
                    DisplayName = "Critical diff",
                    LastUsedUtc = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                    PayloadKind = DiffPlannerPayloadKind.Sanitized,
                    SanitizedDigest = "sha256:abc123",
                },
            },
        };

        await store.SaveAsync(snapshot);

        var loaded = await store.LoadAsync();
        loaded.SchemaVersion.Should().Be(DiffPlannerMruStore.CurrentSchemaVersion);
        loaded.MaxEntries.Should().Be(8);
        loaded.Entries.Should().HaveCount(1);

        var entry = loaded.Entries[0];
        entry.BaselinePath.Should().Be("/configs/baseline.json");
        entry.ComparisonPaths.Should().Equal("/configs/compare-a.json", "/configs/compare-b.json");
        entry.DisplayName.Should().Be("Critical diff");
        entry.PayloadKind.Should().Be(DiffPlannerPayloadKind.Sanitized);
        entry.SanitizedDigest.Should().Be("sha256:abc123");
        entry.LastUsedUtc.Should().Be(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public async Task RecordAsync_deduplicates_and_limits_entries()
    {
        using var temp = new TempDirectory();
        var store = new DiffPlannerMruStore(temp.Path);

        for (var index = 0; index < DiffPlannerMruStore.DefaultEntryLimit + 2; index++)
        {
            var entry = new DiffPlannerMruEntry
            {
                BaselinePath = $"/configs/baseline-{index % 3}.json",
                ComparisonPaths =
                {
                    $"/configs/comparison-{index}.json",
                },
                DisplayName = $"Entry {index}",
                LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-index),
                PayloadKind = index % 2 == 0 ? DiffPlannerPayloadKind.Sanitized : DiffPlannerPayloadKind.Raw,
            };

            await store.RecordAsync(entry);
        }

        // Reinsert one of the earlier combinations with different casing to ensure dedupe is case-insensitive.
        await store.RecordAsync(new DiffPlannerMruEntry
        {
            BaselinePath = "/CONFIGS/BASELINE-1.JSON",
            ComparisonPaths = { "/configs/comparison-4.json" },
            DisplayName = "Updated",
            PayloadKind = DiffPlannerPayloadKind.Raw,
            LastUsedUtc = DateTimeOffset.UtcNow,
        });

        var loaded = await store.LoadAsync();
        loaded.Entries.Should().HaveCount(DiffPlannerMruStore.DefaultEntryLimit);
        loaded.Entries[0].BaselinePath.Should().Be("/CONFIGS/BASELINE-1.JSON");
        loaded.Entries[0].ComparisonPaths.Should().Equal("/configs/comparison-4.json");

        loaded.Entries.Count(entry =>
            entry.ComparisonPaths.Count == 1 &&
            entry.ComparisonPaths[0].Equals("/configs/comparison-4.json", StringComparison.OrdinalIgnoreCase) &&
            entry.BaselinePath.Equals("/configs/baseline-1.json", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_waits_for_migration_to_complete()
    {
        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "legacy", "diff-planner.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "{}");

        var migrationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task ControlledMigration(string legacy, string destination, CancellationToken token)
            => ControlledMigrationAsync(legacy, destination, migrationStarted, migrationRelease, token);

        var store = new DiffPlannerMruStore(temp.Path, legacyPath, ControlledMigration);

        var loadTask = store.LoadAsync();
        await migrationStarted.Task;
        loadTask.IsCompleted.Should().BeFalse();

        migrationRelease.TrySetResult(true);
        var snapshot = await loadTask;
        snapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task Migration_converts_legacy_settings()
    {
        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "legacy.json");

        var legacyPayload = new LegacyDiffPlannerSettings
        {
            BaselinePath = "C:/baseline.json",
            ComparisonPaths = new[] { "C:/compare.json" },
            DisplayName = "Legacy",
            LastUsedUtc = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero),
            PayloadKind = "raw",
            SanitizedDigest = "sha256:legacy",
            MaxEntries = 4,
        };

        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacyPayload, SerializerOptions));

        var store = new DiffPlannerMruStore(temp.Path, legacyPath, null);
        var snapshot = await store.LoadAsync();

        snapshot.MaxEntries.Should().Be(4);
        snapshot.Entries.Should().HaveCount(1);

        var entry = snapshot.Entries.Single();
        entry.BaselinePath.Should().Be("C:/baseline.json");
        entry.ComparisonPaths.Should().Equal("C:/compare.json");
        entry.DisplayName.Should().Be("Legacy");
        entry.PayloadKind.Should().Be(DiffPlannerPayloadKind.Raw);
        entry.SanitizedDigest.Should().Be("sha256:legacy");
        entry.LastUsedUtc.Should().Be(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero));
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

    private sealed class LegacyDiffPlannerSettings
    {
        [JsonPropertyName("baseline_path")]
        public string? BaselinePath { get; set; }

        [JsonPropertyName("comparison_paths")]
        public string[]? ComparisonPaths { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("last_used_utc")]
        public DateTimeOffset? LastUsedUtc { get; set; }

        [JsonPropertyName("payload_kind")]
        public string? PayloadKind { get; set; }

        [JsonPropertyName("sanitized_digest")]
        public string? SanitizedDigest { get; set; }

        [JsonPropertyName("max_entries")]
        public int? MaxEntries { get; set; }
    }

    private static async Task ControlledMigrationAsync(
        string legacy,
        string destination,
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        CancellationToken cancellationToken)
    {
        started.TrySetResult(true);
        await release.Task.ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new DiffPlannerMruSnapshot(), SerializerOptions), cancellationToken);
    }
}
