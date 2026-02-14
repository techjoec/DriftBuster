using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class RunProfileListResult
    {
        [JsonPropertyName("profiles")]
        public RunProfileDefinition[] Profiles { get; set; } = System.Array.Empty<RunProfileDefinition>();
    }
}
