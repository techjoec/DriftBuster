using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanResponse
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public ServerScanResult[] Results { get; set; } = Array.Empty<ServerScanResult>();

        [JsonPropertyName("catalog")]
        public ConfigCatalogEntry[] Catalog { get; set; } = Array.Empty<ConfigCatalogEntry>();

        [JsonPropertyName("drilldown")]
        public ConfigDrilldown[] Drilldown { get; set; } = Array.Empty<ConfigDrilldown>();

        [JsonPropertyName("summary")]
        public ServerScanSummary? Summary { get; set; }
    }
}
