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

        /// <summary>Files skipped because they could not be read; omitted when every file was read.</summary>
        [JsonPropertyName("unreadable_files")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? UnreadableFiles { get; set; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }
}
