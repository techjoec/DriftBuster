using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The installed applications (<c>registry list-apps</c>).</summary>
    public sealed class RegistryAppListResult
    {
        /// <summary>Sorted by lower-cased display name, then hive.</summary>
        [JsonPropertyName("apps")]
        public RegistryApplication[] Apps { get; set; } = [];
    }
}
