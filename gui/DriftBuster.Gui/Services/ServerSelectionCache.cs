using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class ServerSelectionCache
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = SessionCacheService.CurrentSchemaVersion;

        [JsonPropertyName("persist_session")]
        public bool PersistSession { get; set; }

        [JsonPropertyName("servers")]
        public IList<ServerSelectionCacheEntry> Servers { get; set; } = new List<ServerSelectionCacheEntry>();

        [JsonPropertyName("activities")]
        public IList<ActivityCacheEntry> Activities { get; set; } = new List<ActivityCacheEntry>();

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
}
