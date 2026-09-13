using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class HuntPlanTransform
    {
        [JsonPropertyName("token_name")]
        public string TokenName { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = string.Empty;

        [JsonPropertyName("rule_name")]
        public string RuleName { get; set; } = string.Empty;
    }
}
