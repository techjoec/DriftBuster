using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>A schedule's state after <c>schedule mark-complete</c> or <c>schedule skip-until</c>.</summary>
    public sealed class ScheduleStateResult
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("next_run")]
        public string? NextRun { get; set; }

        [JsonPropertyName("pending")]
        public string? Pending { get; set; }
    }
}
