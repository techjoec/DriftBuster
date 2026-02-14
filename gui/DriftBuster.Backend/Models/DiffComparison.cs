using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffComparison
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;

        [JsonPropertyName("plan")]
        public DiffPlan Plan { get; set; } = new();

        [JsonPropertyName("metadata")]
        public DiffMetadata Metadata { get; set; } = new();

        [JsonPropertyName("unified_diff")]
        public string UnifiedDiff { get; set; } = string.Empty;
    }
}
