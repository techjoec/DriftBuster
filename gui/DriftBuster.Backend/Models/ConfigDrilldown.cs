using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ConfigDrilldown
    {
        [JsonPropertyName("config_id")]
        public string ConfigId { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("servers")]
        public ConfigServerDetail[] Servers { get; set; } = Array.Empty<ConfigServerDetail>();

        [JsonPropertyName("baseline_host_id")]
        public string BaselineHostId { get; set; } = string.Empty;

        [JsonPropertyName("diff_before")]
        public string DiffBefore { get; set; } = string.Empty;

        [JsonPropertyName("diff_after")]
        public string DiffAfter { get; set; } = string.Empty;

        [JsonPropertyName("unified_diff")]
        public string UnifiedDiff { get; set; } = string.Empty;

        [JsonPropertyName("has_secrets")]
        public bool HasSecrets { get; set; }

        [JsonPropertyName("has_masked_tokens")]
        public bool HasMaskedTokens { get; set; }

        [JsonPropertyName("has_validation_issues")]
        public bool HasValidationIssues { get; set; }

        [JsonPropertyName("notes")]
        public string[] Notes { get; set; } = Array.Empty<string>();

        [JsonPropertyName("provenance")]
        public string Provenance { get; set; } = string.Empty;

        [JsonPropertyName("drift_count")]
        public int DriftCount { get; set; }

        [JsonPropertyName("last_updated")]
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    }
}
