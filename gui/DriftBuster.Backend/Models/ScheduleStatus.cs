using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One entry of <c>run_profiles_cli schedule list</c>: a schedule from the manifest with its scheduler state.</summary>
    public sealed class ScheduleStatus
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonPropertyName("interval_seconds")]
        public double IntervalSeconds { get; set; }

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = [];

        /// <summary>The manifest's metadata values as JSON decoded them (str, bool, int, float, null, list or dict).</summary>
        [JsonPropertyName("metadata")]
        public IDictionary<string, object?> Metadata { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        [JsonPropertyName("start_at")]
        public string? StartAt { get; set; }

        [JsonPropertyName("next_run")]
        public string? NextRun { get; set; }

        [JsonPropertyName("pending")]
        public string? Pending { get; set; }

        [JsonPropertyName("window")]
        public ScheduleWindowDefinition? Window { get; set; }
    }
}
