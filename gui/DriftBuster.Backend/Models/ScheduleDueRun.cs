using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One run <c>schedule due</c> prints.</summary>
    public sealed class ScheduleDueRun
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonPropertyName("scheduled_for")]
        public string ScheduledFor { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = [];

        /// <summary>The manifest's metadata values as JSON decoded them (string, bool, number, null, list or object).</summary>
        [JsonPropertyName("metadata")]
        public IDictionary<string, object?> Metadata { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
