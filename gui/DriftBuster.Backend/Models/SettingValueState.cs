using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>What a server has for one setting.</summary>
    public enum SettingValueState
    {
        /// <summary>The file has the setting; <see cref="SettingValue.Value"/> holds it unless it is masked.</summary>
        [JsonStringEnumMemberName("value")]
        Value,

        /// <summary>The file is there but does not have the setting.</summary>
        [JsonStringEnumMemberName("not_set")]
        NotSet,

        /// <summary>The server has no such file.</summary>
        [JsonStringEnumMemberName("file_missing")]
        FileMissing,

        /// <summary>The file is there but could not be read.</summary>
        [JsonStringEnumMemberName("unreadable")]
        Unreadable,

        /// <summary>The server itself could not be scanned.</summary>
        [JsonStringEnumMemberName("not_scanned")]
        NotScanned,
    }
}
