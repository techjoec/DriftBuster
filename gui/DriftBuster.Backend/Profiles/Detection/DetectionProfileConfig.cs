using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// A configuration expectation inside a detection profile. Construction and <c>with</c> normalise tags
/// (<see cref="ProfileTags.Normalize"/>), freeze metadata as a read-only copy, and normalise <see cref="Path"/> and
/// <see cref="PathGlob"/> as posix paths. Equality compares every field by value, tags as sets.
/// </summary>
public sealed record DetectionProfileConfig
{
    private readonly string _identifier = string.Empty;
    private readonly string? _path;
    private readonly string? _pathGlob;
    private readonly IReadOnlySet<string> _tags = ProfileTags.Normalize(null);
    private readonly IReadOnlyDictionary<string, object?> _metadata = ProfileMetadata.Empty;

    public DetectionProfileConfig(
        string identifier,
        string? path = null,
        string? pathGlob = null,
        string? application = null,
        string? version = null,
        string? branch = null,
        IEnumerable<string?>? tags = null,
        string? expectedFormat = null,
        string? expectedVariant = null,
        IEnumerable<KeyValuePair<string, object?>>? metadata = null)
    {
        Identifier = identifier;
        Application = application;
        Version = version;
        Branch = branch;
        ExpectedFormat = expectedFormat;
        ExpectedVariant = expectedVariant;
        _tags = ProfileTags.Normalize(tags);
        _metadata = ProfileMetadata.Freeze(metadata);
        Path = path;
        PathGlob = pathGlob;
    }

    public string Identifier
    {
        get => _identifier;
        init => _identifier = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string? Path
    {
        get => _path;
        init => _path = value is null ? null : LexicalPath.PosixStr(value);
    }

    public string? PathGlob
    {
        get => _pathGlob;
        init => _pathGlob = value is null ? null : LexicalPath.PosixStr(value);
    }

    public string? Application { get; init; }

    public string? Version { get; init; }

    public string? Branch { get; init; }

    public IReadOnlySet<string> Tags
    {
        get => _tags;
        init => _tags = ProfileTags.Normalize(value);
    }

    public string? ExpectedFormat { get; init; }

    public string? ExpectedVariant { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata
    {
        get => _metadata;
        init => _metadata = ProfileMetadata.Freeze(value);
    }

    /// <summary>
    /// Every config tag and each <c>application:</c>, <c>version:</c>, <c>branch:</c> tag it names must be provided; then a config with no
    /// <see cref="Path"/> or <see cref="PathGlob"/> applies everywhere, otherwise the posix <paramref name="relativePath"/> must equal
    /// <see cref="Path"/> or match <see cref="PathGlob"/> (<see cref="PathWildcard"/> syntax, <c>*</c> crosses <c>/</c>).
    /// </summary>
    public bool Matches(string? relativePath, IReadOnlySet<string> providedTags)
    {
        ArgumentNullException.ThrowIfNull(providedTags);
        if (Tags.Count > 0 && !Tags.IsSubsetOf(providedTags))
        {
            return false;
        }

        if (!HasTag(providedTags, "application", Application)
            || !HasTag(providedTags, "version", Version)
            || !HasTag(providedTags, "branch", Branch))
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

        var normalised = LexicalPath.PosixStr(relativePath);
        if (!string.IsNullOrEmpty(Path) && string.Equals(normalised, Path, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(PathGlob) && PathWildcard.IsMatch(normalised, PathGlob);
    }

    private static bool HasTag(IReadOnlySet<string> providedTags, string prefix, string? value)
        => value is null || providedTags.Contains(prefix + ":" + value);

    public bool Equals(DetectionProfileConfig? other)
        => other is not null
            && string.Equals(Identifier, other.Identifier, StringComparison.Ordinal)
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && string.Equals(PathGlob, other.PathGlob, StringComparison.Ordinal)
            && string.Equals(Application, other.Application, StringComparison.Ordinal)
            && string.Equals(Version, other.Version, StringComparison.Ordinal)
            && string.Equals(Branch, other.Branch, StringComparison.Ordinal)
            && Tags.SetEquals(other.Tags)
            && string.Equals(ExpectedFormat, other.ExpectedFormat, StringComparison.Ordinal)
            && string.Equals(ExpectedVariant, other.ExpectedVariant, StringComparison.Ordinal)
            && EngineValues.Equal(Metadata, other.Metadata);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Identifier);
}
