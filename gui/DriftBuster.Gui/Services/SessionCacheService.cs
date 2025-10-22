using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _cachePath;

        public SessionCacheService(string? rootDirectory = null)
        {
            var basePath = rootDirectory ?? Path.Combine("artifacts", "cache");
            Directory.CreateDirectory(basePath);
            _cachePath = Path.Combine(basePath, "multi-server.json");
        }

        public async Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<ServerSelectionCache>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        public async Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await using var stream = new FileStream(_cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        public void Clear()
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
    }

    public sealed class ServerSelectionCache
    {
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
}
