using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffResultSummary
    {
        [JsonPropertyName("generated_at")]
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("versions")]
        public string[] Versions { get; set; } = Array.Empty<string>();

        [JsonPropertyName("comparison_count")]
        public int ComparisonCount { get; set; } = 0;

        [JsonPropertyName("comparisons")]
        public DiffComparisonSummary[] Comparisons { get; set; } = Array.Empty<DiffComparisonSummary>();
    }
}
