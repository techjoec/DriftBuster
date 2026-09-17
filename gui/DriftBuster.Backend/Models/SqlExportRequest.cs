using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The arguments of an anonymised SQL snapshot export (<c>driftbuster capture export-sql</c>).</summary>
    public sealed class SqlExportRequest
    {
        /// <summary>SQLite database paths, exported in order.</summary>
        [JsonPropertyName("databases")]
        public string[] Databases { get; set; } = [];

        /// <summary>The directory the snapshots and <c>sql-manifest.json</c> are written to.</summary>
        [JsonPropertyName("output_dir")]
        public string OutputDir { get; set; } = "sql-exports";

        /// <summary>Tables to export; empty exports every table.</summary>
        [JsonPropertyName("tables")]
        public string[] Tables { get; set; } = [];

        /// <summary>Tables never exported.</summary>
        [JsonPropertyName("exclude_tables")]
        public string[] ExcludeTables { get; set; } = [];

        /// <summary><c>table.column</c> entries whose values are replaced by the placeholder.</summary>
        [JsonPropertyName("mask_columns")]
        public string[] MaskColumns { get; set; } = [];

        /// <summary><c>table.column</c> entries whose values are hashed.</summary>
        [JsonPropertyName("hash_columns")]
        public string[] HashColumns { get; set; } = [];

        /// <summary>The text masked columns hold.</summary>
        [JsonPropertyName("placeholder")]
        public string? Placeholder { get; set; } = "[REDACTED]";

        /// <summary>The salt hashed values are salted with.</summary>
        [JsonPropertyName("hash_salt")]
        public string? HashSalt { get; set; } = string.Empty;

        /// <summary>Rows exported per table; null exports every row.</summary>
        [JsonPropertyName("limit")]
        public long? Limit { get; set; }

        /// <summary>The prefix of the snapshot file names.</summary>
        [JsonPropertyName("prefix")]
        public string? Prefix { get; set; } = string.Empty;
    }
}
