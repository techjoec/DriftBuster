using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanScope
    {
        [JsonStringEnumMemberName("all_drives")]
        AllDrives,

        [JsonStringEnumMemberName("single_drive")]
        SingleDrive,

        [JsonStringEnumMemberName("custom_roots")]
        CustomRoots,
    }
}
