using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ServerScanExportOptions
    {
        [JsonPropertyName("include_catalog")]
        public bool IncludeCatalog { get; set; } = true;

        [JsonPropertyName("include_drilldown")]
        public bool IncludeDrilldown { get; set; } = true;

        [JsonPropertyName("include_diffs")]
        public bool IncludeDiffs { get; set; } = true;

        [JsonPropertyName("include_summary")]
        public bool IncludeSummary { get; set; } = true;
    }
}
