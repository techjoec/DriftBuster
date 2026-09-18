using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One server compared with the baseline, in counts and file lists people can read at a glance.</summary>
    public sealed class HostComparisonSummary
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("is_baseline")]
        public bool IsBaseline { get; set; }

        /// <summary>False when the server could not be scanned; <see cref="ScanMessage"/> says why.</summary>
        [JsonPropertyName("scanned")]
        public bool Scanned { get; set; }

        [JsonPropertyName("scan_message")]
        public string ScanMessage { get; set; } = string.Empty;

        [JsonPropertyName("settings_differing")]
        public int SettingsDiffering { get; set; }

        [JsonPropertyName("files_differing")]
        public int FilesDiffering { get; set; }

        /// <summary>Files the baseline has and this server does not.</summary>
        [JsonPropertyName("files_missing")]
        public string[] FilesMissing { get; set; } = Array.Empty<string>();

        /// <summary>Files this server has and the baseline does not.</summary>
        [JsonPropertyName("files_extra")]
        public string[] FilesExtra { get; set; } = Array.Empty<string>();

        [JsonPropertyName("files_unreadable")]
        public string[] FilesUnreadable { get; set; } = Array.Empty<string>();

        /// <summary>True when the server was scanned and nothing differs from the baseline.</summary>
        [JsonPropertyName("matches_baseline")]
        public bool MatchesBaseline { get; set; }
    }
}
