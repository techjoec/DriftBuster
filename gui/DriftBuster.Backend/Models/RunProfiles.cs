using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class RunProfileDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("sources")]
        public string[] Sources { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("baseline")]
        public string? Baseline { get; set; }

        [JsonPropertyName("options")]
        public Dictionary<string, string> Options { get; set; } = new();

        [JsonPropertyName("secret_scanner")]
        public SecretScannerOptions SecretScanner { get; set; } = new();
    }

    public sealed class ScheduleDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonPropertyName("every")]
        public string Every { get; set; } = string.Empty;

        [JsonPropertyName("start_at")]
        public string? StartAt { get; set; }
            = null;

        [JsonPropertyName("window")]
        public ScheduleWindowDefinition? Window { get; set; }
            = null;

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new(System.StringComparer.Ordinal);
    }

    public sealed class ScheduleWindowDefinition
    {
        [JsonPropertyName("start")]
        public string? Start { get; set; }
            = null;

        [JsonPropertyName("end")]
        public string? End { get; set; }
            = null;

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }
            = null;
    }

    public sealed class ScheduleListResult
    {
        [JsonPropertyName("schedules")]
        public ScheduleDefinition[] Schedules { get; set; } = Array.Empty<ScheduleDefinition>();
    }

    public sealed class SecretScannerOptions
    {
        [JsonPropertyName("ignore_rules")]
        public string[] IgnoreRules { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("ignore_patterns")]
        public string[] IgnorePatterns { get; set; } = System.Array.Empty<string>();
    }

    public sealed class OfflineCollectorRequest
    {
        public string PackagePath { get; set; } = string.Empty;

        public Dictionary<string, string> Metadata { get; set; } = new();

        public string? ConfigFileName { get; set; }
    }

    public sealed class OfflineCollectorResult
    {
        public string PackagePath { get; set; } = string.Empty;

        public string ConfigFileName { get; set; } = string.Empty;

        public string ScriptFileName { get; set; } = string.Empty;
    }

    public sealed class RunProfileListResult
    {
        [JsonPropertyName("profiles")]
        public RunProfileDefinition[] Profiles { get; set; } = System.Array.Empty<RunProfileDefinition>();
    }

    public sealed class RunProfileRunResult
    {
        [JsonPropertyName("profile")]
        public RunProfileDefinition Profile { get; set; } = new();

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("output_dir")]
        public string OutputDir { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public RunProfileFileResult[] Files { get; set; } = System.Array.Empty<RunProfileFileResult>();
    }

    public sealed class RunProfileFileResult
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("destination")]
        public string Destination { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
