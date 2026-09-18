using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// What a curation choice points at: a file (a source), a setting in it, or one value of that setting. The file and key are
/// wildcard patterns (<c>*</c> any run of characters, <c>?</c> one character, case-insensitive); an empty file matches every
/// file, an empty key means the whole file, and an empty value hash means any value.
/// </summary>
public sealed record CurationTarget
{
    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The value's fingerprint (<see cref="ValueHashOf"/>); empty for any value.</summary>
    [JsonPropertyName("value_hash")]
    public string ValueHash { get; init; } = string.Empty;

    /// <summary>True when the target is a whole file rather than a setting in it.</summary>
    [JsonIgnore]
    public bool IsSource => Key.Length == 0;

    [JsonIgnore]
    public bool IsValue => ValueHash.Length > 0;

    public bool MatchesFile(string path) => CurationPattern.IsMatch(path, File);

    public bool MatchesSetting(string path, string key) => !IsSource && MatchesFile(path) && CurationPattern.IsMatch(key, Key);

    public bool MatchesValue(string path, string key, string? valueHash) =>
        MatchesSetting(path, key) && (!IsValue || string.Equals(ValueHash, valueHash, StringComparison.Ordinal));

    /// <summary>A value's fingerprint: lowercase hex SHA-256 of its UTF-8 text.</summary>
    public static string ValueHashOf(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
