using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>A rendered report.</summary>
    public sealed class ReportResult
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        /// <summary>The report text: an HTML page, or one JSON record per line.</summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>The file written, when one was requested.</summary>
        [JsonPropertyName("output_path")]
        public string? OutputPath { get; set; }

        [JsonPropertyName("detection_count")]
        public int DetectionCount { get; set; }

        [JsonPropertyName("hunt_hit_count")]
        public int HuntHitCount { get; set; }
    }
}
