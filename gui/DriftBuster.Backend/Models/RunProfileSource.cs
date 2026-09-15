using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>
    /// One source of a run profile: a path or glob, an optional alias naming the directory its files are copied under, whether a
    /// source that matches nothing may be skipped, and exclude patterns matched against each file's relative path and name. In
    /// JSON a source with only a path is a bare string; <see cref="RunProfileSourceJsonConverter"/> reads either form.
    /// </summary>
    [JsonConverter(typeof(RunProfileSourceJsonConverter))]
    public sealed class RunProfileSource
    {
        public RunProfileSource()
        {
        }

        public RunProfileSource(string path)
        {
            Path = path;
        }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("alias")]
        public string? Alias { get; set; }

        [JsonPropertyName("optional")]
        public bool Optional { get; set; }

        [JsonPropertyName("exclude")]
        public string[] Exclude { get; set; } = System.Array.Empty<string>();

        /// <summary>True when only <see cref="Path"/> is set, so the source is written as a bare string.</summary>
        [JsonIgnore]
        public bool IsPathOnly => Alias is null && !Optional && (Exclude is null || Exclude.Length == 0);
    }
}
