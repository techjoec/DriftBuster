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

        /// <summary>True when a curation choice or rule leaves the whole setting out of the differences.</summary>
        [JsonPropertyName("ignored")]
        public bool Ignored { get; set; }

        /// <summary>The groups the setting belongs to, by name.</summary>
        [JsonPropertyName("groups")]
        public string[] Groups { get; set; } = Array.Empty<string>();

        /// <summary>True when the setting is on the review list.</summary>
        [JsonPropertyName("in_review")]
        public bool InReview { get; set; }
    }
}
