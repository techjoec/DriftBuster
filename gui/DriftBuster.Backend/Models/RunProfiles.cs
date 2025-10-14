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
        public Dictionary<string, string> Options { get; set; } = new();
    }

    public sealed class RunProfileListResult
    {
        [JsonPropertyName("profiles")]
        public RunProfileDefinition[] Profiles { get; set; } = System.Array.Empty<RunProfileDefinition>();
    }

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

    public sealed class RunProfileFileResult
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("destination")]
        public string Destination { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
