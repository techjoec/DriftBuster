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
    }

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
    }

    public sealed class DiffPlan
    {
        [JsonPropertyName("before")]
        public string Before { get; set; } = string.Empty;

        [JsonPropertyName("after")]
        public string After { get; set; } = string.Empty;

        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("from_label")]
        public string FromLabel { get; set; } = string.Empty;

        [JsonPropertyName("to_label")]
        public string ToLabel { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("mask_tokens")]
        public string[]? MaskTokens { get; set; }

        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = string.Empty;

        [JsonPropertyName("context_lines")]
        public int ContextLines { get; set; }
    }

    public sealed class DiffMetadata
    {
        [JsonPropertyName("left_path")]
        public string LeftPath { get; set; } = string.Empty;

        [JsonPropertyName("right_path")]
        public string RightPath { get; set; } = string.Empty;

        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("context_lines")]
        public int ContextLines { get; set; }
    }
}
