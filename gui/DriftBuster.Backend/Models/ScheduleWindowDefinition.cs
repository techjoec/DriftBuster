using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ScheduleWindowDefinition
    {
        [JsonPropertyName("start")]
        public string? Start { get; set; }
            = null;

        [JsonPropertyName("end")]
        public string? End { get; set; }
            = null;

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }
            = null;
    }
}
