using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Gui.Services
{
    public sealed class DiffPlannerMruSnapshot
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = DiffPlannerMruStore.CurrentSchemaVersion;

        [JsonPropertyName("max_entries")]
        public int MaxEntries { get; set; } = DiffPlannerMruStore.DefaultEntryLimit;

        [JsonPropertyName("entries")]
        public IList<DiffPlannerMruEntry> Entries { get; set; } = new List<DiffPlannerMruEntry>();
    }
}
