using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class HuntResult
    {
        [JsonPropertyName("directory")]
        public string Directory { get; set; } = string.Empty;

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("hits")]
        public HuntHit[] Hits { get; set; } = System.Array.Empty<HuntHit>();

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }

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

    public sealed class HuntRuleSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("token_name")]
        public string? TokenName { get; set; }

        [JsonPropertyName("keywords")]
        public string[] Keywords { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("patterns")]
        public string[] Patterns { get; set; } = System.Array.Empty<string>();
    }
}
