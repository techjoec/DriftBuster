using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The runs due at the reference time, in scheduled order; each is pending until completed.</summary>
    public sealed class ScheduleDueResult
    {
        [JsonPropertyName("runs")]
        public ScheduleDueRun[] Runs { get; set; } = [];
    }
}
