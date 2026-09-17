using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The outcome of a registry value search.</summary>
    public sealed class RegistrySearchResult
    {
        /// <summary>The roots searched.</summary>
        [JsonPropertyName("roots")]
        public RegistryRootEntry[] Roots { get; set; } = [];

        [JsonPropertyName("hits")]
        public RegistrySearchHit[] Hits { get; set; } = [];
    }
}
