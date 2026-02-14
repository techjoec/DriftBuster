using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffMetadataSummary
    {
        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("context_lines")]
        public int ContextLines { get; set; } = 0;

        [JsonPropertyName("baseline_name")]
        public string BaselineName { get; set; } = string.Empty;

        [JsonPropertyName("comparison_name")]
        public string ComparisonName { get; set; } = string.Empty;
    }
}
