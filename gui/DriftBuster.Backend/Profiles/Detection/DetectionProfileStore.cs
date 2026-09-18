using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// Registry for detection profiles. Duplicate profile names and config identifiers are
/// rejected at registration, <see cref="UpdateProfile"/> replaces a profile copy-on-write, <see cref="RemoveConfig"/> removes
/// one config, <see cref="FindConfig"/> locates the profile owning an identifier and <see cref="Summary"/> gives an overview.
/// </summary>
/// <remarks>
/// A duplicate or missing registration raises <see cref="InvalidOperationException"/>, an unknown profile name
/// <see cref="KeyNotFoundException"/>. Profiles keep dict insertion order: a profile replaced by
/// <see cref="UpdateProfile"/>, or restored after a failed update, moves to the end.
/// </remarks>
public sealed partial class DetectionProfileStore : IProfileMatcher
{
    private readonly OrderedDictionary<string, DetectionProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppliedProfileConfig> _configIndex = new(StringComparer.Ordinal);

    // FromDict only: profiles and configs whose JSON name or id is a list or dict, with the error hashing it raises.
    private Dictionary<object, InvalidDataException>? _unhashable;

    public DetectionProfileStore(IEnumerable<DetectionProfile>? profiles = null)
    {
        foreach (var profile in profiles ?? [])
        {
            RegisterProfile(profile);
        }
    }

    private void ValidateProfile(DetectionProfile profile)
    {
        if (_unhashable is not null && _unhashable.TryGetValue(profile, out var nameError))
        {
            throw nameError;
        }

        if (_profiles.ContainsKey(profile.Name))
        {
            throw new InvalidOperationException($"Profile {EngineRepr.StrRepr(profile.Name)} is already registered");
        }

        var seenLocal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var config in profile.Configs)
        {
            if (_unhashable is not null && _unhashable.TryGetValue(config, out var identifierError))
            {
                throw identifierError;
            }

            var identifier = config.Identifier;
            if (!seenLocal.Add(identifier))
            {
                throw new InvalidOperationException(
                    $"Duplicate config identifier {EngineRepr.StrRepr(identifier)} within profile {EngineRepr.StrRepr(profile.Name)}");
            }

            if (_configIndex.TryGetValue(identifier, out var existing))
            {
                throw new InvalidOperationException(
                    $"Config identifier {EngineRepr.StrRepr(identifier)} already registered under profile {EngineRepr.StrRepr(existing.Profile.Name)}");
            }
        }
    }

    private void IndexProfile(DetectionProfile profile)
    {
        foreach (var config in profile.Configs)
        {
            _configIndex[config.Identifier] = new AppliedProfileConfig(profile, config);
        }
    }

    private void DropProfileIndex(DetectionProfile profile)
    {
        foreach (var config in profile.Configs)
        {
            _configIndex.Remove(config.Identifier);
        }
    }

    /// <summary><c>register_profile</c>: registers <paramref name="profile"/> after enforcing unique names and config identifiers.</summary>
    public void RegisterProfile(DetectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        _profiles[profile.Name] = profile;
        IndexProfile(profile);
    }

    /// <summary>
    /// <c>update_profile</c>: replaces the profile called <paramref name="name"/> with what <paramref name="mutator"/> returns for
    /// it, keeping the indexes intact; when the replacement fails validation the original is restored and the error re-raised.
    /// </summary>
    public DetectionProfile UpdateProfile(string name, Func<DetectionProfile, DetectionProfile>? mutator)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (mutator is null)
        {
            throw new ArgumentNullException(nameof(mutator), "mutator must be callable.");
        }

        if (!_profiles.TryGetValue(name, out var original))
        {
            throw NotRegistered(name);
        }

        var candidate = mutator(original with { })
            ?? throw new InvalidOperationException("mutator must return a ConfigurationProfile instance");

        if (!string.Equals(candidate.Name, original.Name, StringComparison.Ordinal) && _profiles.ContainsKey(candidate.Name))
        {
            throw new InvalidOperationException($"Profile {EngineRepr.StrRepr(candidate.Name)} is already registered");
        }

        DropProfileIndex(original);
        _profiles.Remove(name);

        try
        {
            ValidateProfile(candidate);
        }
        catch (InvalidOperationException)
        {
            _profiles[original.Name] = original;
            IndexProfile(original);
            throw;
        }

        _profiles[candidate.Name] = candidate;
        IndexProfile(candidate);
        return candidate;
    }

    /// <summary><c>remove_profile</c>: <see cref="KeyNotFoundException"/> when <paramref name="name"/> is not registered.</summary>
    public void RemoveProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_profiles.Remove(name, out var profile))
        {
            throw NotRegistered(name);
        }

        DropProfileIndex(profile);
    }

    /// <summary>
    /// <c>remove_config</c>: the profile without <paramref name="configId"/>; <see cref="InvalidOperationException"/> when the
    /// profile has no such config, <see cref="KeyNotFoundException"/> when the profile is not registered.
    /// </summary>
    public DetectionProfile RemoveConfig(string profileName, string configId)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentNullException.ThrowIfNull(configId);

        DetectionProfile Remove(DetectionProfile profile)
        {
            var remaining = profile.Configs.Where(config => !string.Equals(config.Identifier, configId, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == profile.Configs.Count)
            {
                throw new InvalidOperationException(
                    $"Config identifier {EngineRepr.StrRepr(configId)} is not registered under profile {EngineRepr.StrRepr(profile.Name)}");
            }

            return profile with { Configs = remaining };
        }

        try
        {
            return UpdateProfile(profileName, Remove);
        }
        catch (KeyNotFoundException exc)
        {
            throw new KeyNotFoundException(NotRegistered(profileName).Message, exc);
        }
    }

    /// <summary><c>get_profile</c>: <see cref="KeyNotFoundException"/> when <paramref name="name"/> is not registered.</summary>
    public DetectionProfile GetProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _profiles.TryGetValue(name, out var profile) ? profile : throw NotRegistered(name);
    }

    private static KeyNotFoundException NotRegistered(string name) => new($"Profile {EngineRepr.StrRepr(name)} is not registered.");

    /// <summary><c>profiles</c>: every registered profile in registration order.</summary>
    public IReadOnlyList<DetectionProfile> Profiles() => _profiles.Values.ToArray();

    /// <summary><c>applicable_profiles</c>: the profiles that apply to the normalised <paramref name="tags"/>.</summary>
    public IReadOnlyList<DetectionProfile> ApplicableProfiles(IEnumerable<string?>? tags)
    {
        var tagSet = ProfileTags.Normalize(tags);
        return _profiles.Values.Where(profile => profile.AppliesTo(tagSet)).ToArray();
    }

    /// <summary><c>find_config</c>: the profile/config pair registered under <paramref name="identifier"/>, or nothing.</summary>
    public IReadOnlyList<AppliedProfileConfig> FindConfig(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return _configIndex.TryGetValue(identifier, out var match) ? [match] : [];
    }

    /// <summary><c>matching_configs</c>: every config of every applicable profile that matches <paramref name="relativePath"/>.</summary>
    public IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IEnumerable<string?>? tags, string? relativePath)
    {
        var tagSet = ProfileTags.Normalize(tags);
        var matches = new List<AppliedProfileConfig>();
        foreach (var profile in ApplicableProfiles(tagSet))
        {
            matches.AddRange(profile.MatchingConfigs(tagSet, relativePath).Select(config => new AppliedProfileConfig(profile, config)));
        }

        return matches;
    }

    IReadOnlyList<AppliedProfileConfig> IProfileMatcher.MatchingConfigs(IReadOnlySet<string> tags, string? relativePath)
        => MatchingConfigs(tags, relativePath);
}
