using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class RunProfileRunResult
    {
        [JsonPropertyName("profile")]
        public RunProfileDefinition Profile { get; set; } = new();

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("output_dir")]
        public string OutputDir { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public RunProfileFileResult[] Files { get; set; } = System.Array.Empty<RunProfileFileResult>();
    }
}
