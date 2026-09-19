using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanStatus
    {
        [JsonStringEnumMemberName("idle")]
        Idle,

        [JsonStringEnumMemberName("queued")]
        Queued,

        [JsonStringEnumMemberName("running")]
        Running,

        [JsonStringEnumMemberName("succeeded")]
        Succeeded,

        [JsonStringEnumMemberName("failed")]
        Failed,

        [JsonStringEnumMemberName("skipped")]
        Skipped,

        [JsonStringEnumMemberName("cached")]
        Cached,
    }
}
