using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The arguments of a registry value search (<c>registry search</c>).</summary>
    public sealed class RegistrySearchRequest
    {
        /// <summary>The application token whose suggested roots are searched when no root is given.</summary>
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        /// <summary>Keywords a value name or its data is matched against.</summary>
        [JsonPropertyName("keywords")]
        public string[] Keywords { get; set; } = [];

        /// <summary>Python regular expressions a value name or its data is matched against.</summary>
        [JsonPropertyName("patterns")]
        public string[] Patterns { get; set; } = [];

        [JsonPropertyName("max_depth")]
        public long MaxDepth { get; set; } = 12;

        [JsonPropertyName("max_hits")]
        public long MaxHits { get; set; } = 200;

        [JsonPropertyName("time_budget_s")]
        public double TimeBudgetSeconds { get; set; } = 10.0;

        /// <summary>Explicit root descriptors (<c>HKLM\Software\Vendor,view=64</c>).</summary>
        [JsonPropertyName("roots")]
        public string[] Roots { get; set; } = [];
    }
}
