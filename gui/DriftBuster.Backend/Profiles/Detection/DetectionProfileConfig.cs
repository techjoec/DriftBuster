using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// One expected configuration file: where it lives (<see cref="Path"/> exactly, or <see cref="PathGlob"/>, both relative with forward
/// slashes), the tags it needs, what it should detect as, and whether its review flags are suppressed.
/// </summary>
public sealed record DetectionProfileConfig
{
    internal static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>(StringComparer.Ordinal);

    public required string Id { get; init; }

    public string? Path { get; init; }

    public string? PathGlob { get; init; }

    public string? Application { get; init; }

    public string? Version { get; init; }

    public string? Branch { get; init; }

    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    public string? ExpectedFormat { get; init; }

    public string? ExpectedVariant { get; init; }

    /// <summary>Clears <c>needs_review</c> on a matching detection (it gets <c>review_ignored</c> instead).</summary>
    public bool IgnoreReviewFlags { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init => field = value ?? NoMetadata; } = NoMetadata;

    /// <summary>
    /// Every config tag and each <c>application:</c>, <c>version:</c>, <c>branch:</c> tag it names must be among the scan's tags; then a
    /// config with no <see cref="Path"/> or <see cref="PathGlob"/> applies everywhere, otherwise the relative path (forward slashes) must
    /// equal <see cref="Path"/> or match <see cref="PathGlob"/> (<see cref="PathWildcard"/> syntax).
    /// </summary>
    public bool Matches(string? relativePath, IReadOnlySet<string> scanTags)
    {
        ArgumentNullException.ThrowIfNull(scanTags);
        if (!Tags.All(scanTags.Contains) || !HasTag("application", Application) || !HasTag("version", Version) || !HasTag("branch", Branch))
        {
            return false;
        }

        if (Path is null && PathGlob is null)
        {
            return true;
        }

        if (relativePath is null)
        {
            return false;
        }

        var normalised = relativePath.Replace('\\', '/');
        return string.Equals(normalised, Path, StringComparison.Ordinal) || (!string.IsNullOrEmpty(PathGlob) && PathWildcard.IsMatch(normalised, PathGlob));

        bool HasTag(string prefix, string? value) => value is null || scanTags.Contains(prefix + ":" + value);
    }
}
