using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>A registry value a search matched.</summary>
    public sealed class RegistrySearchHit
    {
        [JsonPropertyName("hive")]
        public string Hive { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("value_name")]
        public string ValueName { get; set; } = string.Empty;

        [JsonPropertyName("data_preview")]
        public string DataPreview { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
