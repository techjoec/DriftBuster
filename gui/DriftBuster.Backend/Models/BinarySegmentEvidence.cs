using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary><c>BinarySegmentEvidence</c>: sizes and SHA-256 digests of the two sides of a binary comparison.</summary>
    public sealed class BinarySegmentEvidence
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("before_size")]
        public int BeforeSize { get; set; }

        [JsonPropertyName("after_size")]
        public int AfterSize { get; set; }

        [JsonPropertyName("before_digest")]
        public string BeforeDigest { get; set; } = string.Empty;

        [JsonPropertyName("after_digest")]
        public string AfterDigest { get; set; } = string.Empty;

        [JsonPropertyName("changed")]
        public bool Changed { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
