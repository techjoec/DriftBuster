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

    public sealed class DiffChangeSummary
    {
        [JsonPropertyName("before_digest")]
        public string BeforeDigest { get; set; } = string.Empty;

        [JsonPropertyName("after_digest")]
        public string AfterDigest { get; set; } = string.Empty;

        [JsonPropertyName("diff_digest")]
        public string DiffDigest { get; set; } = string.Empty;

        [JsonPropertyName("before_lines")]
        public int BeforeLines { get; set; } = 0;

        [JsonPropertyName("after_lines")]
        public int AfterLines { get; set; } = 0;

        [JsonPropertyName("added_lines")]
        public int AddedLines { get; set; } = 0;

        [JsonPropertyName("removed_lines")]
        public int RemovedLines { get; set; } = 0;

        [JsonPropertyName("changed_lines")]
        public int ChangedLines { get; set; } = 0;
    }
}
