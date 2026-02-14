using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class DiffChangeSummary
    {
        [JsonPropertyName("before_digest")]
        public string BeforeDigest { get; set; } = string.Empty;

        [JsonPropertyName("after_digest")]
        public string AfterDigest { get; set; } = string.Empty;

        [JsonPropertyName("diff_digest")]
        public string DiffDigest { get; set; } = string.Empty;

        [JsonPropertyName("before_lines")]
        public int BeforeLines { get; set; } = 0;

        [JsonPropertyName("after_lines")]
        public int AfterLines { get; set; } = 0;

        [JsonPropertyName("added_lines")]
        public int AddedLines { get; set; } = 0;

        [JsonPropertyName("removed_lines")]
        public int RemovedLines { get; set; } = 0;

        [JsonPropertyName("changed_lines")]
        public int ChangedLines { get; set; } = 0;
    }
}
