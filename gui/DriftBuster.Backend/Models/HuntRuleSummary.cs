using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
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
