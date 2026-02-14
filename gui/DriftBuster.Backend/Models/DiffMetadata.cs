using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
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
