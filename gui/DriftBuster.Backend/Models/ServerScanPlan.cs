using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanPlan
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public ServerScanScope Scope { get; set; } = ServerScanScope.AllDrives;

        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = Array.Empty<string>();

        [JsonPropertyName("baseline")]
        public ServerScanBaselinePreference Baseline { get; set; } = new();

        [JsonPropertyName("export")]
        public ServerScanExportOptions Export { get; set; } = new();

        [JsonPropertyName("throttle_seconds")]
        public double? ThrottleSeconds { get; set; }

        [JsonPropertyName("cached_at")]
        public DateTimeOffset? CachedAt { get; set; }
    }
}
