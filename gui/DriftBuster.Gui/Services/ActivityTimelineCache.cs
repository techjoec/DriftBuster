using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class ActivityTimelineCache
    {
        [JsonPropertyName("filter")]
        public string? Filter { get; set; }

        [JsonPropertyName("last_opened_host")]
        public string? LastOpenedHostId { get; set; }
    }
}
