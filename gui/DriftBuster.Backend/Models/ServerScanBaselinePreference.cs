using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanBaselinePreference
    {
        [JsonPropertyName("is_preferred")]
        public bool IsPreferred { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = "auto";
    }
}
