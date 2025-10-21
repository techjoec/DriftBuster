using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanScope
    {
        [EnumMember(Value = "all_drives")]
        AllDrives,

        [EnumMember(Value = "single_drive")]
        SingleDrive,

        [EnumMember(Value = "custom_roots")]
        CustomRoots,
    }

    public enum ServerScanStatus
    {
        [EnumMember(Value = "idle")]
        Idle,

        [EnumMember(Value = "queued")]
        Queued,

        [EnumMember(Value = "running")]
        Running,

        [EnumMember(Value = "succeeded")]
        Succeeded,

        [EnumMember(Value = "failed")]
        Failed,

        [EnumMember(Value = "skipped")]
        Skipped,

        [EnumMember(Value = "cached")]
        Cached,
    }

    public enum ServerAvailabilityStatus
    {
        [EnumMember(Value = "unknown")]
        Unknown,

        [EnumMember(Value = "found")]
        Found,

        [EnumMember(Value = "not_found")]
        NotFound,

        [EnumMember(Value = "permission_denied")]
        PermissionDenied,

        [EnumMember(Value = "offline")]
        Offline,
    }

    public enum ConfigPresenceStatus
    {
        [EnumMember(Value = "unknown")]
        Unknown,

        [EnumMember(Value = "found")]
        Found,

        [EnumMember(Value = "not_found")]
        NotFound,

        [EnumMember(Value = "permission_denied")]
        PermissionDenied,

        [EnumMember(Value = "offline")]
        Offline,
    }

    public sealed class ServerScanBaselinePreference
    {
        [JsonPropertyName("is_preferred")]
        public bool IsPreferred { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = "auto";
    }

    public sealed class ServerScanExportOptions
    {
        [JsonPropertyName("include_catalog")]
        public bool IncludeCatalog { get; set; } = true;

        [JsonPropertyName("include_drilldown")]
        public bool IncludeDrilldown { get; set; } = true;

        [JsonPropertyName("include_diffs")]
        public bool IncludeDiffs { get; set; } = true;

        [JsonPropertyName("include_summary")]
        public bool IncludeSummary { get; set; } = true;
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

        [JsonPropertyName("baseline")]
        public ServerScanBaselinePreference Baseline { get; set; } = new();

        [JsonPropertyName("export")]
        public ServerScanExportOptions Export { get; set; } = new();

        [JsonPropertyName("throttle_seconds")]
        public double? ThrottleSeconds { get; set; }

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

        [JsonPropertyName("availability")]
        public ServerAvailabilityStatus Availability { get; set; } = ServerAvailabilityStatus.Unknown;
    }

    public sealed class ServerScanResponse
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public ServerScanResult[] Results { get; set; } = Array.Empty<ServerScanResult>();

        [JsonPropertyName("catalog")]
        public ConfigCatalogEntry[] Catalog { get; set; } = Array.Empty<ConfigCatalogEntry>();

        [JsonPropertyName("drilldown")]
        public ConfigDrilldown[] Drilldown { get; set; } = Array.Empty<ConfigDrilldown>();

        [JsonPropertyName("summary")]
        public ServerScanSummary? Summary { get; set; }
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

        [JsonPropertyName("presence_status")]
        public ConfigPresenceStatus PresenceStatus { get; set; } = ConfigPresenceStatus.Unknown;
    }

    public sealed class ServerScanSummary
    {
        [JsonPropertyName("baseline_host_id")]
        public string BaselineHostId { get; set; } = string.Empty;

        [JsonPropertyName("total_hosts")]
        public int TotalHosts { get; set; }

        [JsonPropertyName("configs_evaluated")]
        public int ConfigsEvaluated { get; set; }

        [JsonPropertyName("drifting_configs")]
        public int DriftingConfigs { get; set; }

        [JsonPropertyName("generated_at")]
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
