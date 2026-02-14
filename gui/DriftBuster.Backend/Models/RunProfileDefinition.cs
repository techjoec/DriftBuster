using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class RunProfileDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("sources")]
        public string[] Sources { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("baseline")]
        public string? Baseline { get; set; }

        [JsonPropertyName("options")]
        public IDictionary<string, string> Options { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonPropertyName("secret_scanner")]
        public SecretScannerOptions SecretScanner { get; set; } = new();
    }
}
