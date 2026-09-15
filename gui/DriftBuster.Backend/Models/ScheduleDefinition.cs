using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    public sealed class ScheduleDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonPropertyName("every")]
        public string Every { get; set; } = string.Empty;

        [JsonPropertyName("start_at")]
        public string? StartAt { get; set; }
            = null;

        [JsonPropertyName("window")]
        public ScheduleWindowDefinition? Window { get; set; }
            = null;

        [JsonPropertyName("tags")]
        public string[] Tags { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("metadata")]
        public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>
        /// The manifest's <c>every</c> value when it is not a string (a number of seconds), as <c>json.loads</c> reads it; it is written back
        /// in place of <see cref="Every"/> while <see cref="Every"/> still shows its <c>str()</c> text.
        /// </summary>
        [JsonIgnore]
        public object? EveryValue { get; set; }

        /// <summary>
        /// The manifest's metadata values that are not strings (numbers, booleans, null, lists, objects), by key, as <c>json.loads</c> reads
        /// them; each is written back in place of its <see cref="Metadata"/> text while that text still shows it.
        /// </summary>
        [JsonIgnore]
        public IDictionary<string, object?>? MetadataValues { get; set; }

        /// <summary>
        /// The manifest entry the card was read from, as <c>json.loads</c> reads it. Each field whose card text still shows what the entry held
        /// is written back as the entry held it, and keys the card does not show are kept.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, object?>? ManifestEntry { get; set; }
    }
}
