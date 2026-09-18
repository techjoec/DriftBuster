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

        [JsonPropertyName("drift")]
        public string? Drift { get; set; }

        [JsonPropertyName("search")]
        public string? Search { get; set; }
    }
}
