using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
    public interface ISessionCacheService
    {
        Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default);

        Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default);

        void Clear();
    }

    public sealed class SessionCacheService : ISessionCacheService
    {
        internal const int CurrentSchemaVersion = 2;
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly string _cachePath;
        private readonly SemaphoreSlim _cacheLock;
        private readonly Task _migrationTask;

        public SessionCacheService(string? rootDirectory = null)
            : this(
                rootDirectory,
                rootDirectory is null ? Path.Combine("artifacts", "cache", "multi-server.json") : null,
                null)
        {
        }

        internal SessionCacheService(
            string? rootDirectory,
            string? legacyCachePath,
            Func<string, string, CancellationToken, Task>? migrationHandler)
        {
            var basePath = rootDirectory ?? DriftbusterPaths.GetSessionDirectory();

            Directory.CreateDirectory(basePath);

            var cachePath = Path.GetFullPath(Path.Combine(basePath, "multi-server.json"));
            _cachePath = cachePath;
            _cacheLock = GetLockForPath(cachePath);

            if (legacyCachePath is null)
            {
                _migrationTask = Task.CompletedTask;
                return;
            }

            var migrate = migrationHandler ?? MigrateLegacyCacheAsync;
            var legacyPath = Path.GetFullPath(legacyCachePath);
            _migrationTask = migrate(legacyPath, _cachePath, CancellationToken.None);
        }

        public async Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _migrationTask.ConfigureAwait(false);

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return null;
                }

                await using var stream = new FileStream(
                    _cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                return await JsonSerializer.DeserializeAsync<ServerSelectionCache>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            await _migrationTask.ConfigureAwait(false);

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                await using var stream = new FileStream(
                    _cachePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public void Clear()
        {
            if (!_migrationTask.IsCompleted)
            {
                _migrationTask.GetAwaiter().GetResult();
            }

            _cacheLock.Wait();
            try
            {
                if (File.Exists(_cachePath))
                {
                    File.Delete(_cachePath);
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private static async Task MigrateLegacyCacheAsync(string legacyPath, string destination, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(legacyPath))
                {
                    return;
                }

                if (File.Exists(destination))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var cacheLock = GetLockForPath(destination);
                await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var source = new FileStream(
                        legacyPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: true);
                    await using var target = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        useAsync: true);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    SessionCacheMigrationCounters.RecordSuccess();
                }
                finally
                {
                    cacheLock.Release();
                }
            }
            catch
            {
                SessionCacheMigrationCounters.RecordFailure();
                // Best-effort migration for developer caches; ignore failures.
            }
        }

        private static SemaphoreSlim GetLockForPath(string path)
        {
            return PathLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        }
    }

    internal static class SessionCacheMigrationCounters
    {
        private static int _migrationSuccessCount;
        private static int _migrationFailureCount;

        public static int Successes => Volatile.Read(ref _migrationSuccessCount);

        public static int Failures => Volatile.Read(ref _migrationFailureCount);

        public static void RecordSuccess()
        {
            Interlocked.Increment(ref _migrationSuccessCount);
        }

        public static void RecordFailure()
        {
            Interlocked.Increment(ref _migrationFailureCount);
        }

        public static void Reset()
        {
            Volatile.Write(ref _migrationSuccessCount, 0);
            Volatile.Write(ref _migrationFailureCount, 0);
        }
    }

    public sealed class ServerSelectionCache
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = SessionCacheService.CurrentSchemaVersion;

        [JsonPropertyName("persist_session")]
        public bool PersistSession { get; set; }

        [JsonPropertyName("servers")]
        public List<ServerSelectionCacheEntry> Servers { get; set; } = new();

        [JsonPropertyName("activities")]
        public List<ActivityCacheEntry> Activities { get; set; } = new();

        [JsonPropertyName("catalog_sort")]
        public CatalogSortCache? CatalogSort { get; set; }

        [JsonPropertyName("activity_filter")]
        public string? ActivityFilter { get; set; }

        [JsonPropertyName("catalog_filters")]
        public CatalogFilterCache? CatalogFilters { get; set; }

        [JsonPropertyName("timeline")]
        public ActivityTimelineCache? Timeline { get; set; }

        [JsonPropertyName("active_view")]
        public string? ActiveView { get; set; }
    }

    public sealed class ServerSelectionCacheEntry
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("scope")]
        public ServerScanScope Scope { get; set; } = ServerScanScope.AllDrives;

        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = Array.Empty<string>();
    }

    public sealed class ActivityCacheEntry
    {
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }

    public sealed class CatalogSortCache
    {
        [JsonPropertyName("column")]
        public string Column { get; set; } = string.Empty;

        [JsonPropertyName("descending")]
        public bool Descending { get; set; }
    }

    public sealed class CatalogFilterCache
    {
        [JsonPropertyName("coverage")]
        public string? Coverage { get; set; }

        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("baseline")]
        public string? Baseline { get; set; }

        [JsonPropertyName("search")]
        public string? Search { get; set; }
    }

    public sealed class ActivityTimelineCache
    {
        [JsonPropertyName("filter")]
        public string? Filter { get; set; }

        [JsonPropertyName("last_opened_host")]
        public string? LastOpenedHostId { get; set; }
    }
}
