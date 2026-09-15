using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The schedules <c>run_profiles_cli schedule list</c> prints, ordered by name.</summary>
    public sealed class ScheduleStatusListResult
    {
        [JsonPropertyName("schedules")]
        public ScheduleStatus[] Schedules { get; set; } = [];
    }
}
