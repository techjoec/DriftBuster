using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>One server's value for one setting.</summary>
    public sealed class SettingValue
    {
        [JsonPropertyName("host_id")]
        public string HostId { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public SettingValueState State { get; set; }

        /// <summary>The value as text; null unless <see cref="State"/> is <see cref="SettingValueState.Value"/> and the value is not masked.</summary>
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        /// <summary>True when the value is a secret: it is compared but never shown.</summary>
        [JsonPropertyName("masked")]
        public bool Masked { get; set; }

        /// <summary>True when this server's value or state differs from the baseline server's.</summary>
        [JsonPropertyName("differs")]
        public bool DiffersFromBaseline { get; set; }
    }
}
