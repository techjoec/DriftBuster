using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// A pattern over files and settings that names the files it matches (application, friendly file name, description) and can
/// act on the matching settings: ignore, mask or unmask them, or put them in groups. An empty key pattern makes the rule act
/// on whole files: ignore leaves the whole file out.
/// </summary>
public sealed record CurationRule
{
    public string Name { get; init => field = value ?? string.Empty; } = string.Empty;

    // Required in the file: a source-generated read would give an absent flag false, not this default.
    [JsonRequired]
    public bool Enabled { get; init; } = true;

    public string Scope { get; init => field = value ?? string.Empty; } = string.Empty;

    public string FilePattern { get; init => field = value ?? string.Empty; } = string.Empty;

    public string KeyPattern { get; init => field = value ?? string.Empty; } = string.Empty;

    public string AppName { get; init => field = value ?? string.Empty; } = string.Empty;

    public string FileLabel { get; init => field = value ?? string.Empty; } = string.Empty;

    public string Description { get; init => field = value ?? string.Empty; } = string.Empty;

    public bool Ignore { get; init; }

    /// <summary>Empty, <see cref="CurationChoiceKinds.Mask"/> or <see cref="CurationChoiceKinds.Unmask"/>.</summary>
    public string Mask { get; init => field = value ?? string.Empty; } = string.Empty;

    public IReadOnlyList<string> Groups { get; init => field = value ?? []; } = [];

    public bool MatchesFile(string path) => CurationPattern.IsMatch(path, FilePattern);

    public bool MatchesSetting(string path, string key) => KeyPattern.Length > 0 && MatchesFile(path) && CurationPattern.IsMatch(key, KeyPattern);
}
