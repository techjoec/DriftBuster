using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerAvailabilityStatus
    {
        [JsonStringEnumMemberName("unknown")]
        Unknown,

        [JsonStringEnumMemberName("found")]
        Found,

        [JsonStringEnumMemberName("not_found")]
        NotFound,

        [JsonStringEnumMemberName("permission_denied")]
        PermissionDenied,

        [JsonStringEnumMemberName("offline")]
        Offline,
    }
}
