using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffPlanSummary
    {
        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("from_label")]
        public string? FromLabel { get; set; } = string.Empty;

        [JsonPropertyName("to_label")]
        public string? ToLabel { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; set; } = string.Empty;

        [JsonPropertyName("mask_tokens")]
        public string[] MaskTokens { get; set; } = Array.Empty<string>();

        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = string.Empty;

        [JsonPropertyName("context_lines")]
        public int ContextLines { get; set; } = 0;
    }
}
