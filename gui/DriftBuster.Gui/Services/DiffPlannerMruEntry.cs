using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class DiffPlannerMruEntry
    {
        [JsonPropertyName("baseline_path")]
        public string BaselinePath { get; set; } = string.Empty;

        [JsonPropertyName("comparison_paths")]
        public IList<string> ComparisonPaths { get; set; } = new List<string>();

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("last_used_utc")]
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("payload_kind")]
        public DiffPlannerPayloadKind PayloadKind { get; set; } = DiffPlannerPayloadKind.Unknown;

        [JsonPropertyName("sanitized_digest")]
        public string? SanitizedDigest { get; set; }
    }
}
