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

        /// <summary>Tokens and replacement counts, ordered by token; null when not recorded.</summary>
        [JsonPropertyName("redaction_counts")]
        public OrderedDictionary<string, int>? RedactionCounts { get; set; }

        /// <summary>Binary comparison evidence; null when not recorded.</summary>
        [JsonPropertyName("binary_evidence")]
        public BinarySegmentEvidence[]? BinaryEvidence { get; set; }

        /// <summary>The clamps applied to the canonical payloads and the diff; null when nothing was truncated.</summary>
        [JsonPropertyName("safety_limits")]
        public DriftBuster.Backend.Diff.DiffSafetyLimits? SafetyLimits { get; set; }
    }
}
