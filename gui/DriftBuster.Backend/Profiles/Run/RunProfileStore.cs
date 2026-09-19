using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Run profiles on disk: <c>&lt;base&gt;/Profiles/&lt;safe name&gt;/profile.json</c>, read strictly through <see cref="ModelJson"/> and
/// validated before every save. A file that cannot be read raises <see cref="RunProfileException"/> naming the file and JSON path.
/// </summary>
public static class RunProfileStore
{
    public const string FileName = "profile.json";

    /// <summary><c>&lt;base or working directory&gt;/Profiles</c>, created.</summary>
    public static string ProfilesRoot(string? baseDir = null)
        => Directory.CreateDirectory(Path.Join(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, "Profiles")).FullName;

    public static string ProfileDirectory(string profileName, string? baseDir = null)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return Path.Join(ProfilesRoot(baseDir), SafeName(profileName));
    }

    /// <summary>Every character that is not a letter, digit, <c>-</c> or <c>_</c> becomes <c>-</c>.</summary>
    public static string SafeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_' ? rune.ToString() : "-");
        }

        return builder.ToString();
    }

    /// <inheritdoc cref="PathExpansion.Expand"/>
    public static string Expand(string path) => PathExpansion.Expand(path);

    public static RunProfileDefinition Load(string profileName, string? baseDir = null)
    {
        var path = Path.Join(ProfileDirectory(profileName, baseDir), FileName);
        return File.Exists(path) ? Read(path) : throw new RunProfileException($"Profile not found: {profileName}");
    }

    /// <summary>A profile file anywhere.</summary>
    public static RunProfileDefinition Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, ModelJson.TypeInfo<RunProfileDefinition>())
                ?? throw new RunProfileException($"{path}: the file holds null.");
        }
        catch (JsonException exc)
        {
            throw new RunProfileException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
    }

    /// <summary>Validates, writes <c>profile.json</c> and returns the profile directory.</summary>
    public static string Save(RunProfileDefinition profile, string? baseDir = null)
    {
        Validate(profile);
        var directory = Directory.CreateDirectory(ProfileDirectory(profile.Name, baseDir)).FullName;
        AtomicFile.WriteAllText(Path.Join(directory, FileName), ModelJson.Serialize(profile));
        return directory;
    }

    /// <summary>Every profile under the root, by directory name.</summary>
    public static IReadOnlyList<RunProfileDefinition> List(string? baseDir = null, CancellationToken cancellationToken = default)
    {
        var profiles = new List<RunProfileDefinition>();
        foreach (var directory in Directory.EnumerateDirectories(ProfilesRoot(baseDir)).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Join(directory, FileName);
            if (File.Exists(path))
            {
                profiles.Add(Read(path));
            }
        }

        return profiles;
    }

    /// <summary>
    /// A name; at least one source, none blank; a baseline that is one of the source paths; every source that is not optional and has
    /// no wildcard existing; every ignore pattern a valid regular expression.
    /// </summary>
    public static void Validate(RunProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new RunProfileException("name: required.");
        }

        if (profile.Sources.Count == 0)
        {
            throw new RunProfileException("sources: at least one source is required.");
        }

        for (var index = 0; index < profile.Sources.Count; index++)
        {
            var source = profile.Sources[index];
            if (string.IsNullOrWhiteSpace(source.Path))
            {
                throw new RunProfileException($"sources[{index}].path: required.");
            }

            if (!source.Optional && !PathWildcard.HasWildcards(source.Path) && !Exists(Expand(source.Path)))
            {
                throw new RunProfileException($"sources[{index}].path: does not exist: {source.Path}");
            }
        }

        if (!string.IsNullOrEmpty(profile.Baseline) && !profile.Sources.Any(source => string.Equals(source.Path, profile.Baseline, StringComparison.Ordinal)))
        {
            throw new RunProfileException("baseline: must be one of the source paths.");
        }

        for (var index = 0; index < profile.SecretScanner.IgnorePatterns.Count; index++)
        {
            try
            {
                _ = PatternRegex.Create(profile.SecretScanner.IgnorePatterns[index]);
            }
            catch (RegexParseException exc)
            {
                throw new RunProfileException($"secret_scanner.ignore_patterns[{index}]: {exc.Message}", exc);
            }
        }
    }

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
