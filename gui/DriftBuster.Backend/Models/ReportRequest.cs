using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The arguments of a report over a scanned tree: detections and, optionally, hunt hits, rendered as HTML or JSON lines.</summary>
    public sealed class ReportRequest
    {
        /// <summary><c>html</c> or <c>jsonl</c>.</summary>
        [JsonPropertyName("format")]
        public string Format { get; set; } = "html";

        /// <summary>The file or directory to scan.</summary>
        [JsonPropertyName("root")]
        public string Root { get; set; } = ".";

        /// <summary>The detection and hunt walk's glob.</summary>
        [JsonPropertyName("glob")]
        public string Glob { get; set; } = "**/*";

        /// <summary>Runs the default hunt rules and reports their hits.</summary>
        [JsonPropertyName("include_hunt")]
        public bool IncludeHunt { get; set; } = true;

        /// <summary>The HTML report's title.</summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = "DriftBuster Report";

        /// <summary>Sensitive tokens redacted from the report.</summary>
        [JsonPropertyName("mask_tokens")]
        public string[] MaskTokens { get; set; } = [];

        /// <summary>The text redacted tokens are replaced with.</summary>
        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = "[REDACTED]";

        /// <summary>A file the report is also written to.</summary>
        [JsonPropertyName("output_path")]
        public string? OutputPath { get; set; }
    }
}
