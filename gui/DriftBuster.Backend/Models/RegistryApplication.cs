using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>An installed application read from a registry Uninstall key.</summary>
    public sealed class RegistryApplication
    {
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("key_path")]
        public string KeyPath { get; set; } = string.Empty;

        [JsonPropertyName("hive")]
        public string Hive { get; set; } = string.Empty;

        [JsonPropertyName("publisher")]
        public string? Publisher { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("uninstall_string")]
        public string? UninstallString { get; set; }

        [JsonPropertyName("install_location")]
        public string? InstallLocation { get; set; }

        /// <summary><c>32</c>, <c>64</c> or <c>auto</c>.</summary>
        [JsonPropertyName("view")]
        public string View { get; set; } = "auto";
    }
}
