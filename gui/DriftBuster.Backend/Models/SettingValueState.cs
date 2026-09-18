using System.Runtime.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>What a server has for one setting.</summary>
    public enum SettingValueState
    {
        /// <summary>The file has the setting; <see cref="SettingValue.Value"/> holds it unless it is masked.</summary>
        [EnumMember(Value = "value")]
        Value,

        /// <summary>The file is there but does not have the setting.</summary>
        [EnumMember(Value = "not_set")]
        NotSet,

        /// <summary>The server has no such file.</summary>
        [EnumMember(Value = "file_missing")]
        FileMissing,

        /// <summary>The file is there but could not be read.</summary>
        [EnumMember(Value = "unreadable")]
        Unreadable,

        /// <summary>The server itself could not be scanned.</summary>
        [EnumMember(Value = "not_scanned")]
        NotScanned,
    }
}
