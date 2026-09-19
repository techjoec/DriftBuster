using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>
/// Detection profiles by name with a config index: profile names are unique, and config ids are unique across the store.
/// </summary>
public sealed class DetectionProfileStore : IProfileMatcher
{
    private readonly List<DetectionProfile> _profiles = [];

    public DetectionProfileStore(IEnumerable<DetectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!names.Add(profile.Name))
            {
                throw new DetectionProfileException($"Profile '{profile.Name}' is defined twice.");
            }

            foreach (var config in profile.Configs)
            {
                if (!ids.TryAdd(config.Id, profile.Name))
                {
                    throw new DetectionProfileException($"Config id '{config.Id}' in profile '{profile.Name}' is already used by profile '{ids[config.Id]}'.");
                }
            }

            _profiles.Add(profile);
        }
    }

    public IReadOnlyList<DetectionProfile> Profiles => _profiles;

    /// <summary>A store file, read strictly.</summary>
    public static DetectionProfileStore Load(string path)
        => new(Read(path, ModelJson.TypeInfo<DetectionProfileStoreFile>()).Profiles);

    /// <summary>The configs of every applicable profile that match <paramref name="relativePath"/>, in store order.</summary>
    public IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IReadOnlySet<string> tags, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return [.. _profiles
            .Where(profile => profile.AppliesTo(tags))
            .SelectMany(profile => profile.Configs.Where(config => config.Matches(relativePath, tags)).Select(config => new AppliedProfileConfig(profile, config)))];
    }

    /// <summary>Profiles by name with their tags and config ids, and the totals.</summary>
    public DetectionProfileSummary Summary()
    {
        var profiles = _profiles
            .OrderBy(profile => profile.Name, StringComparer.Ordinal)
            .Select(profile => new DetectionProfileSummaryEntry(
                profile.Name,
                profile.Description,
                [.. profile.Tags.Order(StringComparer.Ordinal)],
                [.. profile.Configs.Select(config => config.Id)]))
            .ToArray();
        return new DetectionProfileSummary(profiles.Length, profiles.Sum(profile => profile.ConfigIds.Count), profiles);
    }

    /// <summary>A file of <typeparamref name="T"/>, read strictly; failures name the file and the JSON path.</summary>
    internal static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? throw new DetectionProfileException($"{path}: the file holds null.");
        }
        catch (JsonException exc)
        {
            throw new DetectionProfileException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new DetectionProfileException($"{path}: {exc.Message}", exc);
        }
    }
}
