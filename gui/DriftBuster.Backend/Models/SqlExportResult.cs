using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The outcome of an SQL snapshot export.</summary>
    public sealed class SqlExportResult
    {
        /// <summary>0 when every database was exported, 1 when any was missing or failed.</summary>
        [JsonPropertyName("exit_code")]
        public int ExitCode { get; set; }

        /// <summary>The lines the export reported (one per snapshot written).</summary>
        [JsonPropertyName("output")]
        public string Output { get; set; } = string.Empty;

        /// <summary>The errors the export reported (missing or failed databases).</summary>
        [JsonPropertyName("errors")]
        public string Errors { get; set; } = string.Empty;

        /// <summary>The <c>sql-manifest.json</c> written.</summary>
        [JsonPropertyName("manifest_path")]
        public string ManifestPath { get; set; } = string.Empty;

        /// <summary>The manifest as written.</summary>
        [JsonPropertyName("manifest_json")]
        public string ManifestJson { get; set; } = string.Empty;

        /// <summary>Each snapshot file written, in database order.</summary>
        [JsonPropertyName("snapshot_paths")]
        public string[] SnapshotPaths { get; set; } = [];
    }
}
