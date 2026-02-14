using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffComparisonSummary
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;

        [JsonPropertyName("plan")]
        public DiffPlanSummary Plan { get; set; } = new();

        [JsonPropertyName("metadata")]
        public DiffMetadataSummary Metadata { get; set; } = new();

        [JsonPropertyName("summary")]
        public DiffChangeSummary Summary { get; set; } = new();
    }
}
