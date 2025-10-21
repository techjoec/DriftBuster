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
}
