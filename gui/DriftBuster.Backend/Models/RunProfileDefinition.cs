using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class RunProfileDefinition
    {
        // UI Automation reads a list item through ToString.
        public override string ToString() => Name;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("sources")]
        public RunProfileSource[] Sources { get; set; } = System.Array.Empty<RunProfileSource>();

        [JsonPropertyName("baseline")]
        public string? Baseline { get; set; }

        [JsonPropertyName("options")]
        public IDictionary<string, string> Options { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonPropertyName("secret_scanner")]
        public SecretScannerOptions SecretScanner { get; set; } = new();
    }
}
