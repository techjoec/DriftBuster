using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One setting across every server.</summary>
    public sealed class SettingRow
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>True when any server differs from the baseline for this setting.</summary>
        [JsonPropertyName("differs")]
        public bool Differs { get; set; }

        [JsonPropertyName("values")]
        public SettingValue[] Values { get; set; } = Array.Empty<SettingValue>();
    }
}
