using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanResult
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public ServerScanStatus Status { get; set; } = ServerScanStatus.Idle;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = Array.Empty<string>();

        [JsonPropertyName("used_cache")]
        public bool UsedCache { get; set; }

        [JsonPropertyName("availability")]
        public ServerAvailabilityStatus Availability { get; set; } = ServerAvailabilityStatus.Unknown;
    }
}
