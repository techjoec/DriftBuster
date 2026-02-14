using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class OfflineCollectorResult
    {
        public string PackagePath { get; set; } = string.Empty;

        public string ConfigFileName { get; set; } = string.Empty;

        public string ScriptFileName { get; set; } = string.Empty;
    }
}
