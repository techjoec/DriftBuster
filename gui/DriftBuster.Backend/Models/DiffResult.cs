using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffResult
    {
        [JsonPropertyName("versions")]
        public string[] Versions { get; set; } = Array.Empty<string>();

        [JsonPropertyName("comparisons")]
        public DiffComparison[] Comparisons { get; set; } = Array.Empty<DiffComparison>();

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;

        [JsonIgnore]
        public string SanitizedJson { get; set; } = string.Empty;

        [JsonIgnore]
        public DiffResultSummary? Summary { get; set; }
    }
}
