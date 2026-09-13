using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class HuntHitMetadata
    {
        [JsonPropertyName("plan_transform")]
        public HuntPlanTransform? PlanTransform { get; set; }
    }
}
