using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ScheduleListResult
    {
        [JsonPropertyName("schedules")]
        public ScheduleDefinition[] Schedules { get; set; } = Array.Empty<ScheduleDefinition>();
    }
}
