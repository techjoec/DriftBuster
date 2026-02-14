using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;

namespace DriftBuster.Gui.Services
{
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

                var stream = new FileStream(
                    _cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    return await JsonSerializer.DeserializeAsync<ServerSelectionCache>(stream, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
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
                var stream = new FileStream(
                    _cachePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
                }
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
                    var source = new FileStream(
                        legacyPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: true);
                    await using (source.ConfigureAwait(false))
                    {
                        var target = new FileStream(
                            destination,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            useAsync: true);
                        await using (target.ConfigureAwait(false))
                        {
                            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        }
                    }
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
}
