using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One configuration file compared setting by setting across servers.</summary>
    public sealed class FileComparison
    {
        /// <summary>The catalog config id, for opening the file's details; empty when no server's copy could be read.</summary>
        [JsonPropertyName("config_id")]
        public string ConfigId { get; set; } = string.Empty;

        /// <summary>The file's path relative to the scanned root, with forward slashes.</summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        /// <summary><c>settings</c>, <c>lines</c> or <c>binary</c>: how the file's settings were read.</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        /// <summary>How many settings differ on at least one server.</summary>
        [JsonPropertyName("settings_differing")]
        public int SettingsDiffering { get; set; }

        /// <summary>True when any setting differs or the file is missing or unreadable somewhere.</summary>
        [JsonPropertyName("differs")]
        public bool Differs { get; set; }

        /// <summary>Each server's copy of the file: <c>present</c>, <c>file_missing</c>, <c>unreadable</c> or <c>not_scanned</c>.</summary>
        [JsonPropertyName("presence")]
        public SettingValue[] Presence { get; set; } = Array.Empty<SettingValue>();

        [JsonPropertyName("settings")]
        public SettingRow[] Settings { get; set; } = Array.Empty<SettingRow>();
    }
}
