using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class CatalogFilterCache
    {
        [JsonPropertyName("coverage")]
        public string? Coverage { get; set; }

        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("baseline")]
        public string? Baseline { get; set; }

        [JsonPropertyName("search")]
        public string? Search { get; set; }
    }
}
