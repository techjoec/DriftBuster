using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The scan's files compared setting by setting against the baseline server.</summary>
    public sealed class SettingsComparison
    {
        [JsonPropertyName("baseline_host_id")]
        public string BaselineHostId { get; set; } = string.Empty;

        /// <summary>One summary per server, in plan order.</summary>
        [JsonPropertyName("hosts")]
        public HostComparisonSummary[] Hosts { get; set; } = Array.Empty<HostComparisonSummary>();

        /// <summary>Every file found on any server, ordered by path.</summary>
        [JsonPropertyName("files")]
        public FileComparison[] Files { get; set; } = Array.Empty<FileComparison>();
    }
}
