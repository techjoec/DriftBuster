using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ConfigCatalogEntry
    {
        [JsonPropertyName("config_id")]
        public string ConfigId { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("drift_count")]
        public int DriftCount { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "none";

        [JsonPropertyName("present_hosts")]
        public string[] PresentHosts { get; set; } = Array.Empty<string>();

        [JsonPropertyName("missing_hosts")]
        public string[] MissingHosts { get; set; } = Array.Empty<string>();

        [JsonPropertyName("last_updated")]
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("has_secrets")]
        public bool HasSecrets { get; set; }

        [JsonPropertyName("has_masked_tokens")]
        public bool HasMaskedTokens { get; set; }

        [JsonPropertyName("has_validation_issues")]
        public bool HasValidationIssues { get; set; }

        [JsonPropertyName("coverage_status")]
        public string CoverageStatus { get; set; } = "full";
    }
}
