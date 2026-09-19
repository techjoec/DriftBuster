using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public enum DiffPlannerPayloadKind
    {
        [JsonStringEnumMemberName("unknown")]
        Unknown = 0,

        [JsonStringEnumMemberName("sanitized")]
        Sanitized = 1,

        [JsonStringEnumMemberName("raw")]
        Raw = 2,
    }
}
