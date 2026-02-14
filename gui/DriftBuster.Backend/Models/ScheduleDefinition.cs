using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ScheduleDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonPropertyName("every")]
        public string Every { get; set; } = string.Empty;

        [JsonPropertyName("start_at")]
        public string? StartAt { get; set; }
            = null;

        [JsonPropertyName("window")]
        public ScheduleWindowDefinition? Window { get; set; }
            = null;

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("metadata")]
        public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(System.StringComparer.Ordinal);
    }
}
