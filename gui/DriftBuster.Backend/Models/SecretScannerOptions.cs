using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class SecretScannerOptions
    {
        [JsonPropertyName("ignore_rules")]
        public string[] IgnoreRules { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("ignore_patterns")]
        public string[] IgnorePatterns { get; set; } = System.Array.Empty<string>();
    }
}
