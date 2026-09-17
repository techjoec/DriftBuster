using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The outcome of a capture run.</summary>
    public sealed class CaptureRunResult
    {
        /// <summary>0 when the snapshot and manifest were written, 1 when a check refused the run.</summary>
        [JsonPropertyName("exit_code")]
        public int ExitCode { get; set; }

        /// <summary>The lines the run reported.</summary>
        [JsonPropertyName("output")]
        public string Output { get; set; } = string.Empty;

        /// <summary>The refusal, guardrail warnings and redaction warning the run reported.</summary>
        [JsonPropertyName("errors")]
        public string Errors { get; set; } = string.Empty;

        /// <summary>The snapshot written.</summary>
        [JsonPropertyName("snapshot_path")]
        public string? SnapshotPath { get; set; }

        /// <summary>The manifest written.</summary>
        [JsonPropertyName("manifest_path")]
        public string? ManifestPath { get; set; }

        /// <summary>The manifest as written.</summary>
        [JsonPropertyName("manifest_json")]
        public string? ManifestJson { get; set; }
    }
}
