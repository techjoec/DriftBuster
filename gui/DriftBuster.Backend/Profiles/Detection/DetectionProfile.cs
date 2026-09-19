namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// A named set of configuration expectations. A profile without tags applies to every scan; otherwise every tag must be among the
/// scan's tags.
/// </summary>
public sealed record DetectionProfile
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    // Source-generated reads pass null for an absent init-only member, so the setters restore the empty defaults.
    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    public IReadOnlyList<DetectionProfileConfig> Configs { get; init => field = value ?? []; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init => field = value ?? DetectionProfileConfig.NoMetadata; } = DetectionProfileConfig.NoMetadata;

    public bool AppliesTo(IReadOnlySet<string> scanTags)
    {
        ArgumentNullException.ThrowIfNull(scanTags);
        return Tags.All(scanTags.Contains);
    }
}
