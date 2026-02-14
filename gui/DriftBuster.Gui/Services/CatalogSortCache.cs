using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class CatalogSortCache
    {
        [JsonPropertyName("column")]
        public string Column { get; set; } = string.Empty;

        [JsonPropertyName("descending")]
        public bool Descending { get; set; }
    }
}
