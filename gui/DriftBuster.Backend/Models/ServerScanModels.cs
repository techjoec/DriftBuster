using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanScope
    {
        [JsonPropertyName("all_drives")]
        AllDrives,

        [JsonPropertyName("single_drive")]
        SingleDrive,

        [JsonPropertyName("custom_roots")]
        CustomRoots,
    }

    public enum ServerScanStatus
    {
        [JsonPropertyName("idle")]
        Idle,

        [JsonPropertyName("queued")]
        Queued,

        [JsonPropertyName("running")]
        Running,

        [JsonPropertyName("succeeded")]
        Succeeded,

        [JsonPropertyName("failed")]
        Failed,

        [JsonPropertyName("skipped")]
        Skipped,

        [JsonPropertyName("cached")]
        Cached,
    }

    public sealed class ServerScanPlan
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public ServerScanScope Scope { get; set; } = ServerScanScope.AllDrives;

        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = Array.Empty<string>();

        [JsonPropertyName("cached_at")]
        public DateTimeOffset? CachedAt { get; set; }
    }

    public sealed class ScanProgress
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public ServerScanStatus Status { get; set; } = ServerScanStatus.Idle;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class ServerScanResult
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public ServerScanStatus Status { get; set; } = ServerScanStatus.Idle;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = Array.Empty<string>();

        [JsonPropertyName("used_cache")]
        public bool UsedCache { get; set; }
    }

    public sealed class ServerScanResponse
    {
        [JsonPropertyName("results")]
        public ServerScanResult[] Results { get; set; } = Array.Empty<ServerScanResult>();

        [JsonPropertyName("catalog")]
        public ConfigCatalogEntry[] Catalog { get; set; } = Array.Empty<ConfigCatalogEntry>();

        [JsonPropertyName("drilldown")]
        public ConfigDrilldown[] Drilldown { get; set; } = Array.Empty<ConfigDrilldown>();
    }

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

    public sealed class ConfigServerDetail
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("present")]
        public bool Present { get; set; }

        [JsonPropertyName("is_baseline")]
        public bool IsBaseline { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("drift_lines")]
        public int DriftLineCount { get; set; }

        [JsonPropertyName("has_secrets")]
        public bool HasSecrets { get; set; }

        [JsonPropertyName("masked")]
        public bool Masked { get; set; }

        [JsonPropertyName("redaction_status")]
        public string RedactionStatus { get; set; } = string.Empty;

        [JsonPropertyName("last_seen")]
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
}
