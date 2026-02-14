using System;
using System.Text.Json.Serialization;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
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
}
