using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class ActivityCacheEntry
    {
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }
}
