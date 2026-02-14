using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
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
}
