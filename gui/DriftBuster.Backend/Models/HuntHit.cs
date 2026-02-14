using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class HuntHit
    {
        [JsonPropertyName("rule")]
        public HuntRuleSummary Rule { get; set; } = new();

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("relative_path")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonPropertyName("line_number")]
        public int LineNumber { get; set; }

        [JsonPropertyName("excerpt")]
        public string Excerpt { get; set; } = string.Empty;
    }
}
