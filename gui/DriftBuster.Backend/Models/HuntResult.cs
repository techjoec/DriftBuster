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
}
