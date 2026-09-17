using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The arguments of a capture run (<c>capture.py run</c>).</summary>
    public sealed class CaptureRunRequest
    {
        /// <summary>The file or directory to scan.</summary>
        [JsonPropertyName("root")]
        public string Root { get; set; } = ".";

        /// <summary>A detection profile store JSON payload; null scans without profiles.</summary>
        [JsonPropertyName("profiles_path")]
        public string? ProfilesPath { get; set; }

        /// <summary>Tags activating profiles.</summary>
        [JsonPropertyName("profile_tags")]
        public string[] ProfileTags { get; set; } = [];

        /// <summary>The detection walk's glob.</summary>
        [JsonPropertyName("glob")]
        public string Glob { get; set; } = "**/*";

        /// <summary>The hunt walk's glob.</summary>
        [JsonPropertyName("hunt_glob")]
        public string HuntGlob { get; set; } = "**/*";

        /// <summary>Glob patterns the hunt skips.</summary>
        [JsonPropertyName("hunt_exclude")]
        public string[] HuntExclude { get; set; } = [];

        /// <summary>Skips the hunt.</summary>
        [JsonPropertyName("skip_hunt")]
        public bool SkipHunt { get; set; }

        /// <summary>Bytes sampled from each file.</summary>
        [JsonPropertyName("sample_size")]
        public long SampleSize { get; set; } = 128 * 1024;

        /// <summary>The directory the snapshot and manifest are written to.</summary>
        [JsonPropertyName("output_dir")]
        public string OutputDir { get; set; } = "captures";

        /// <summary>The capture identifier; null uses the UTC time.</summary>
        [JsonPropertyName("capture_id")]
        public string? CaptureId { get; set; }

        /// <summary>The operator recorded; null reads <c>DRIFTBUSTER_CAPTURE_OPERATOR</c>, <c>USER</c> or <c>USERNAME</c>.</summary>
        [JsonPropertyName("operator")]
        public string? Operator { get; set; }

        /// <summary>The environment label (required).</summary>
        [JsonPropertyName("environment")]
        public string? Environment { get; set; }

        /// <summary>The reason for the capture (required).</summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// <summary>Sensitive tokens redacted from the snapshot.</summary>
        [JsonPropertyName("mask_tokens")]
        public string[] MaskTokens { get; set; } = [];

        /// <summary>The text redacted tokens are replaced with.</summary>
        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = "[REDACTED]";

        /// <summary>Allows a capture without mask tokens.</summary>
        [JsonPropertyName("allow_unmasked")]
        public bool AllowUnmasked { get; set; }

        /// <summary><c>registry_scan.json</c> files summarised into the manifest.</summary>
        [JsonPropertyName("registry_scans")]
        public string[] RegistryScans { get; set; } = [];
    }
}
