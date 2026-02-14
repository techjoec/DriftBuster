using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanStatus
    {
        [EnumMember(Value = "idle")]
        Idle,

        [EnumMember(Value = "queued")]
        Queued,

        [EnumMember(Value = "running")]
        Running,

        [EnumMember(Value = "succeeded")]
        Succeeded,

        [EnumMember(Value = "failed")]
        Failed,

        [EnumMember(Value = "skipped")]
        Skipped,

        [EnumMember(Value = "cached")]
        Cached,
    }
}
