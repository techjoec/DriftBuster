using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The outcome of comparing two capture snapshots (<c>driftbuster capture compare</c>).</summary>
    public sealed class CaptureCompareResult
    {
        /// <summary>0 for a comparison or a missing baseline, 1 when the current snapshot is missing or unreadable.</summary>
        [JsonPropertyName("exit_code")]
        public int ExitCode { get; set; }

        /// <summary>The comparison summary as text.</summary>
        [JsonPropertyName("output")]
        public string Output { get; set; } = string.Empty;

        /// <summary>The error that stopped the comparison.</summary>
        [JsonPropertyName("errors")]
        public string Errors { get; set; } = string.Empty;

        /// <summary>The comparison as JSON (added, removed and changed keys, profile diff, tokens); null when nothing was compared.</summary>
        [JsonPropertyName("comparison_json")]
        public string? ComparisonJson { get; set; }
    }
}
