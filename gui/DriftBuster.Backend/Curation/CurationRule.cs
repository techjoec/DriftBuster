using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// A pattern over files and settings that names the files it matches (application, friendly file name, description) and can
/// act on the matching settings: ignore, mask or unmask them, or put them in groups. An empty key pattern makes the rule act
/// on whole files: ignore leaves the whole file out.
/// </summary>
public sealed record CurationRule
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("file_pattern")]
    public string FilePattern { get; init; } = string.Empty;

    [JsonPropertyName("key_pattern")]
    public string KeyPattern { get; init; } = string.Empty;

    [JsonPropertyName("app_name")]
    public string AppName { get; init; } = string.Empty;

    [JsonPropertyName("file_label")]
    public string FileLabel { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("ignore")]
    public bool Ignore { get; init; }

    /// <summary>Empty, <see cref="CurationChoiceKinds.Mask"/> or <see cref="CurationChoiceKinds.Unmask"/>.</summary>
    [JsonPropertyName("mask")]
    public string Mask { get; init; } = string.Empty;

    [JsonPropertyName("groups")]
    public IReadOnlyList<string> Groups { get; init; } = [];

    public bool MatchesFile(string path) => CurationPattern.IsMatch(path, FilePattern);

    public bool MatchesSetting(string path, string key) => KeyPattern.Length > 0 && MatchesFile(path) && CurationPattern.IsMatch(key, KeyPattern);
}
