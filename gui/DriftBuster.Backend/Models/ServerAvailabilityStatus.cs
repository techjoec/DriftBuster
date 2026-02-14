using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerAvailabilityStatus
    {
        [EnumMember(Value = "unknown")]
        Unknown,

        [EnumMember(Value = "found")]
        Found,

        [EnumMember(Value = "not_found")]
        NotFound,

        [EnumMember(Value = "permission_denied")]
        PermissionDenied,

        [EnumMember(Value = "offline")]
        Offline,
    }
}
