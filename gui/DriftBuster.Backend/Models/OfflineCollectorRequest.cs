using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class OfflineCollectorRequest
    {
        public string PackagePath { get; set; } = string.Empty;

        public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public string? ConfigFileName { get; set; }
    }
}
