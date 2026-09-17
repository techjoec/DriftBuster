using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>A registry root: hive, key path and optional view.</summary>
    public sealed class RegistryRootEntry
    {
        [JsonPropertyName("hive")]
        public string Hive { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary><c>32</c>, <c>64</c> or null.</summary>
        [JsonPropertyName("view")]
        public string? View { get; set; }
    }
}
