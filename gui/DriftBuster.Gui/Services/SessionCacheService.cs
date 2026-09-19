using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Json;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// The Multi-server session in <c>multi-server.json</c> under the session directory, read strictly: a file that cannot be read
    /// raises <see cref="InvalidDataException"/> naming the file and JSON path, and is never replaced.
    /// </summary>
    public sealed class SessionCacheService : ISessionCacheService
    {
        internal const int CurrentSchemaVersion = 3;
        internal const string FileName = "multi-server.json";

        private readonly SemaphoreSlim _lock = new(1, 1);

        public SessionCacheService(string? rootDirectory = null)
        {
            CachePath = Path.GetFullPath(Path.Join(rootDirectory ?? DriftbusterPaths.GetSessionDirectory(), FileName));
        }

        public string CachePath { get; }

        /// <summary>The saved session, or null when there is none.</summary>
        public ServerSelectionCache? Read()
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var snapshot = ModelJson.ReadFile(CachePath, GuiJson.TypeInfo<ServerSelectionCache>());
            return snapshot.SchemaVersion == CurrentSchemaVersion
                ? snapshot
                : throw new InvalidDataException($"{CachePath}: $.schema_version: {snapshot.SchemaVersion} is not the supported version {CurrentSchemaVersion}.");
        }

        public async Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return Read();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ModelJson.WriteFile(CachePath, snapshot, GuiJson.TypeInfo<ServerSelectionCache>());
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Clear()
        {
            _lock.Wait();
            try
            {
                if (File.Exists(CachePath))
                {
                    File.Delete(CachePath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
