using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// Registry for detection profiles (the port of <c>ProfileStore</c>). Duplicate profile names and config identifiers are
/// rejected at registration, <see cref="UpdateProfile"/> replaces a profile copy-on-write, <see cref="RemoveConfig"/> removes
/// one config, <see cref="FindConfig"/> locates the profile owning an identifier and <see cref="Summary"/> gives an overview.
/// </summary>
/// <remarks>
/// Python's errors map to: <c>ValueError</c> <see cref="PythonValueException"/>, <c>TypeError</c>
/// <see cref="PythonTypeException"/>, <c>KeyError</c> <see cref="KeyNotFoundException"/> whose message is <c>str()</c> of the
/// <c>KeyError</c> (the repr of its argument). Profiles keep dict insertion order: a profile replaced by
/// <see cref="UpdateProfile"/>, or restored after a failed update, moves to the end.
/// </remarks>
public sealed partial class DetectionProfileStore : IProfileMatcher
{
    private readonly OrderedDictionary<string, DetectionProfile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppliedProfileConfig> _configIndex = new(StringComparer.Ordinal);

    // FromDict only: profiles and configs whose JSON name or id is a list or dict, with the TypeError hashing it raises.
    private Dictionary<object, PythonTypeException>? _unhashable;

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
            throw new PythonValueException($"Profile {PythonRepr.StrRepr(profile.Name)} is already registered", nameof(profile));
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
                throw new PythonValueException(
                    $"Duplicate config identifier {PythonRepr.StrRepr(identifier)} within profile {PythonRepr.StrRepr(profile.Name)}",
                    nameof(profile));
            }

            if (_configIndex.TryGetValue(identifier, out var existing))
            {
                throw new PythonValueException(
                    $"Config identifier {PythonRepr.StrRepr(identifier)} already registered under profile {PythonRepr.StrRepr(existing.Profile.Name)}",
                    nameof(profile));
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
            throw new PythonTypeException("mutator must be callable", nameof(mutator));
        }

        if (!_profiles.TryGetValue(name, out var original))
        {
            throw new KeyNotFoundException(PythonRepr.StrRepr($"Profile {PythonRepr.StrRepr(name)} is not registered"));
        }

        var candidate = mutator(original with { })
            ?? throw new PythonTypeException("mutator must return a ConfigurationProfile instance", nameof(mutator));

        if (!string.Equals(candidate.Name, original.Name, StringComparison.Ordinal) && _profiles.ContainsKey(candidate.Name))
        {
            throw new PythonValueException($"Profile {PythonRepr.StrRepr(candidate.Name)} is already registered", nameof(mutator));
        }

        DropProfileIndex(original);
        _profiles.Remove(name);

        try
        {
            ValidateProfile(candidate);
        }
        catch (PythonValueException)
        {
            _profiles[original.Name] = original;
            IndexProfile(original);
            throw;
        }

        _profiles[candidate.Name] = candidate;
        IndexProfile(candidate);
        return candidate;
    }

    /// <summary><c>remove_profile</c>: <c>KeyError</c> when <paramref name="name"/> is not registered.</summary>
    public void RemoveProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_profiles.Remove(name, out var profile))
        {
            throw new KeyNotFoundException(PythonRepr.StrRepr(name));
        }

        DropProfileIndex(profile);
    }

    /// <summary>
    /// <c>remove_config</c>: the profile without <paramref name="configId"/>; <c>ValueError</c> when the profile has no such config,
    /// <c>KeyError</c> when the profile is not registered.
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
                throw new PythonValueException(
                    $"Config identifier {PythonRepr.StrRepr(configId)} is not registered under profile {PythonRepr.StrRepr(profile.Name)}",
                    nameof(configId));
            }

            return profile with { Configs = remaining };
        }

        try
        {
            return UpdateProfile(profileName, Remove);
        }
        catch (KeyNotFoundException exc)
        {
            throw new KeyNotFoundException(PythonRepr.StrRepr($"Profile {PythonRepr.StrRepr(profileName)} is not registered"), exc);
        }
    }

    /// <summary><c>get_profile</c>: <c>KeyError</c> when <paramref name="name"/> is not registered.</summary>
    public DetectionProfile GetProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _profiles.TryGetValue(name, out var profile) ? profile : throw new KeyNotFoundException(PythonRepr.StrRepr(name));
    }

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
