using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ConfigServerDetail
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("present")]
        public bool Present { get; set; }

        [JsonPropertyName("is_baseline")]
        public bool IsBaseline { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("drift_lines")]
        public int DriftLineCount { get; set; }

        [JsonPropertyName("has_secrets")]
        public bool HasSecrets { get; set; }

        [JsonPropertyName("masked")]
        public bool Masked { get; set; }

        [JsonPropertyName("redaction_status")]
        public string RedactionStatus { get; set; } = string.Empty;

        [JsonPropertyName("last_seen")]
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("presence_status")]
        public ConfigPresenceStatus PresenceStatus { get; set; } = ConfigPresenceStatus.Unknown;
    }
}
