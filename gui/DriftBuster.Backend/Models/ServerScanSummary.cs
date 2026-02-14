using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanSummary
    {
        [JsonPropertyName("baseline_host_id")]
        public string BaselineHostId { get; set; } = string.Empty;

        [JsonPropertyName("total_hosts")]
        public int TotalHosts { get; set; }

        [JsonPropertyName("configs_evaluated")]
        public int ConfigsEvaluated { get; set; }

        [JsonPropertyName("drifting_configs")]
        public int DriftingConfigs { get; set; }

        [JsonPropertyName("generated_at")]
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
