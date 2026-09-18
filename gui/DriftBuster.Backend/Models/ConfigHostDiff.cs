using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One server's copy of a file against the baseline's: its text and the unified diff between the two.</summary>
    public sealed class ConfigHostDiff
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("after")]
        public string After { get; set; } = string.Empty;

        [JsonPropertyName("unified_diff")]
        public string UnifiedDiff { get; set; } = string.Empty;
    }
}
