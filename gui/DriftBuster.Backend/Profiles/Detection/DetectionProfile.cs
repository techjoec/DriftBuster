using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// A named set of configuration expectations (the port of <c>ConfigurationProfile</c>). Construction and <c>with</c> apply
/// <c>__post_init__</c>: tags normalised, configs copied, metadata frozen. Equality is the dataclass's, field by field.
/// </summary>
public sealed record DetectionProfile
{
    private readonly string _name = string.Empty;
    private readonly IReadOnlySet<string> _tags = ProfileTags.Normalize(null);
    private readonly IReadOnlyList<DetectionProfileConfig> _configs = [];
    private readonly IReadOnlyDictionary<string, object?> _metadata = ProfileMetadata.Empty;

    public DetectionProfile(
        string name,
        string? description = null,
        IEnumerable<string?>? tags = null,
        IEnumerable<DetectionProfileConfig>? configs = null,
        IEnumerable<KeyValuePair<string, object?>>? metadata = null)
    {
        Name = name;
        Description = description;
        _tags = ProfileTags.Normalize(tags);
        _configs = (configs ?? []).ToArray().AsReadOnly();
        _metadata = ProfileMetadata.Freeze(metadata);
    }

    public string Name
    {
        get => _name;
        init => _name = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string? Description { get; init; }

    public IReadOnlySet<string> Tags
    {
        get => _tags;
        init => _tags = ProfileTags.Normalize(value);
    }

    public IReadOnlyList<DetectionProfileConfig> Configs
    {
        get => _configs;
        init => _configs = (value ?? []).ToArray().AsReadOnly();
    }

    public IReadOnlyDictionary<string, object?> Metadata
    {
        get => _metadata;
        init => _metadata = ProfileMetadata.Freeze(value);
    }

    /// <summary><c>applies_to</c>: a profile without tags applies to every tag set, otherwise its tags must all be provided.</summary>
    public bool AppliesTo(IReadOnlySet<string> providedTags)
    {
        ArgumentNullException.ThrowIfNull(providedTags);
        return Tags.Count == 0 || Tags.IsSubsetOf(providedTags);
    }

    /// <summary><c>matching_configs</c>: the configs that match <paramref name="relativePath"/> under <paramref name="providedTags"/>, in order.</summary>
    public IReadOnlyList<DetectionProfileConfig> MatchingConfigs(IReadOnlySet<string> providedTags, string? relativePath)
        => Configs.Where(config => config.Matches(relativePath, providedTags)).ToArray();

    public bool Equals(DetectionProfile? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Description, other.Description, StringComparison.Ordinal)
            && Tags.SetEquals(other.Tags)
            && Configs.SequenceEqual(other.Configs)
            && PythonValues.Equal(Metadata, other.Metadata);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);
}
