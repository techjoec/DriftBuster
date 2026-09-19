using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>The <c>profile</c> operations for the console tool: <c>create</c>, <c>list</c>, <c>show</c> and <c>run</c>.</summary>
public static class RunProfileCommands
{
    /// <summary><c>key=value</c> split at the first <c>=</c>, both sides trimmed; a later key replaces an earlier one.</summary>
    public static IReadOnlyDictionary<string, string> ParseOptions(IEnumerable<string> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new RunProfileException($"Invalid option '{pair}': use key=value.");
            }

            options[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        return options;
    }

    /// <summary>Trimmed values, blanks and repeats dropped, first-occurrence order.</summary>
    public static IReadOnlyList<string> CleanValues(IEnumerable<string?>? values)
        => [.. (values ?? []).OfType<string>().Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>The profile's ignore lists with <paramref name="ignoreRules"/> and <paramref name="ignorePatterns"/> appended (repeats skipped).</summary>
    public static RunProfileDefinition WithSecretOverrides(RunProfileDefinition profile, IEnumerable<string?>? ignoreRules, IEnumerable<string?>? ignorePatterns)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with
        {
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = CleanValues([.. profile.SecretScanner.IgnoreRules, .. ignoreRules ?? []]),
                IgnorePatterns = CleanValues([.. profile.SecretScanner.IgnorePatterns, .. ignorePatterns ?? []]),
            },
        };
    }

    /// <summary>A profile from the arguments, saved and returned.</summary>
    public static RunProfileDefinition Create(
        string name,
        string? description,
        IEnumerable<string>? sources,
        string? baseline,
        IEnumerable<string>? options,
        IEnumerable<string?>? ignoreRules,
        IEnumerable<string?>? ignorePatterns,
        string? baseDir)
    {
        var profile = new RunProfileDefinition
        {
            Name = name,
            Description = description,
            Sources = [.. (sources ?? []).Select(path => new RunProfileSource { Path = path })],
            Baseline = baseline,
            Options = ParseOptions(options ?? []),
            SecretScanner = new SecretScannerOptions { IgnoreRules = CleanValues(ignoreRules), IgnorePatterns = CleanValues(ignorePatterns) },
        };
        RunProfileStore.Save(profile, baseDir);
        return profile;
    }

    /// <summary>Lines for <c>list</c>: <c>- name description</c>, or <c>No profiles found.</c>.</summary>
    public static IReadOnlyList<string> ListProfileLines(string? baseDir, CancellationToken cancellationToken = default)
    {
        var profiles = RunProfileStore.List(baseDir, cancellationToken);
        return profiles.Count == 0
            ? ["No profiles found."]
            : [.. profiles.Select(profile => $"- {profile.Name} {profile.Description}".TrimEnd())];
    }

    /// <summary>
    /// The profile from <paramref name="profilePath"/> or by <paramref name="name"/>, with the secret overrides, saved when
    /// <paramref name="save"/> is set, then run.
    /// </summary>
    public static RunProfileRunResult Run(
        string? profilePath,
        string? name,
        string? baseDir,
        bool save,
        string? timestamp,
        IEnumerable<string?>? ignoreRules,
        IEnumerable<string?>? ignorePatterns,
        CancellationToken cancellationToken = default)
    {
        var profile = !string.IsNullOrEmpty(profilePath)
            ? RunProfileStore.Read(profilePath)
            : RunProfileStore.Load(name ?? throw new ArgumentNullException(nameof(name)), baseDir);
        return new RunProfileExecutor(baseDir).Execute(WithSecretOverrides(profile, ignoreRules, ignorePatterns), save, timestamp, cancellationToken);
    }
}
