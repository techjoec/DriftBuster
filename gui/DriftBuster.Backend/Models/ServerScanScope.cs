using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public enum ServerScanScope
    {
        [EnumMember(Value = "all_drives")]
        AllDrives,

        [EnumMember(Value = "single_drive")]
        SingleDrive,

        [EnumMember(Value = "custom_roots")]
        CustomRoots,
    }
}
