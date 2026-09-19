using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Run-profile commands for the console tool: <c>--option</c> and secret-ignore handling, and <c>create</c>, <c>list</c>, <c>show</c>,
/// <c>run</c>. Parsing, printing and exit codes stay in the CLI.
/// </summary>
public static class RunProfileCommands
{
    /// <summary><c>key=value</c> split at the first "=", both sides trimmed; a pair without "=" throws <see cref="CommandExitException"/>.</summary>
    public static OrderedDictionary<string, object?> ParseOptions(IEnumerable<string> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in pairs)
        {
            var separator = item.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new CommandExitException($"Invalid option format: {EngineRepr.StrRepr(item)}. Use key=value.");
            }

            options[EngineText.Strip(item[..separator])] = EngineText.Strip(item[(separator + 1)..]);
        }

        return options;
    }

    /// <summary>Trimmed values, blanks and repeats dropped, first-occurrence order.</summary>
    public static IReadOnlyList<string> CleanSecretValues(IEnumerable<string?>? values) => CleanedValues(values);

    private static List<string> CleanedValues(IEnumerable<string?>? values)
    {
        var cleaned = new List<string>();
        foreach (var value in values ?? [])
        {
            if (value is null)
            {
                continue;
            }

            var text = EngineText.Strip(value);
            if (text.Length > 0 && !cleaned.Contains(text, StringComparer.Ordinal))
            {
                cleaned.Add(text);
            }
        }

        return cleaned;
    }

    /// <summary><c>ignore_rules</c> and <c>ignore_patterns</c>, each only when non-empty after cleaning.</summary>
    public static OrderedDictionary<string, object?> BuildSecretScannerPayload(IEnumerable<string?>? ignoreRules, IEnumerable<string?>? ignorePatterns)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var rules = CleanedValues(ignoreRules);
        var patterns = CleanedValues(ignorePatterns);
        if (rules.Count > 0)
        {
            payload["ignore_rules"] = rules.Cast<object?>().ToList();
        }

        if (patterns.Count > 0)
        {
            payload["ignore_patterns"] = patterns.Cast<object?>().ToList();
        }

        return payload;
    }

    /// <summary>
    /// The profile unchanged without overrides; otherwise each override list appended to the cleaned existing list (repeats skipped)
    /// and read back through <see cref="RunProfile.FromDict"/>, keeping the original <see cref="RunProfile.SecretOptions"/> (a structured
    /// profile's raw option values do not survive <see cref="RunProfile.ToDict"/>).
    /// </summary>
    public static RunProfile ApplySecretOverrides(RunProfile profile, IEnumerable<string?>? ignoreRules, IEnumerable<string?>? ignorePatterns)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var overrides = BuildSecretScannerPayload(ignoreRules, ignorePatterns);
        if (overrides.Count == 0)
        {
            return profile;
        }

        var payload = profile.ToDict();
        var existingPayload = payload["secret_scanner"] as OrderedDictionary<string, object?>;
        var existing = new OrderedDictionary<string, object?>(existingPayload ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (key, values) in overrides)
        {
            var current = existing.GetValueOrDefault(key) is List<object?> list ? list.Select(item => (string?)item) : null;
            var merged = CleanedValues(current);
            foreach (var value in (List<object?>)values!)
            {
                if (!merged.Contains((string)value!, StringComparer.Ordinal))
                {
                    merged.Add((string)value!);
                }
            }

            existing[key] = merged.Cast<object?>().ToList();
        }

        payload["secret_scanner"] = existing;
        return RunProfile.FromDict(payload).WithSecretOptions(profile.SecretOptions);
    }

    /// <summary>A profile from the arguments (string sources, parsed options, secret scanner payload), saved and returned.</summary>
    public static RunProfile Create(
        string name,
        string? description,
        IEnumerable<string>? sources,
        string? baseline,
        IEnumerable<string>? options,
        IEnumerable<string?>? ignoreRules,
        IEnumerable<string?>? ignorePatterns,
        string? baseDir)
    {
        var profile = new RunProfile(
            name,
            description,
            (sources ?? []).Select(source => new RunProfileSource(source)),
            baseline,
            ParseOptions(options ?? []),
            BuildSecretScannerPayload(ignoreRules, ignorePatterns));
        RunProfileStore.SaveProfile(profile, baseDir);
        return profile;
    }

    /// <summary>Lines for <c>list</c>: "- name description" right-trimmed, or "No profiles found.".</summary>
    public static IReadOnlyList<string> ListProfileLines(string? baseDir, CancellationToken cancellationToken = default)
    {
        var profiles = RunProfileStore.ListProfiles(baseDir, cancellationToken);
        return profiles.Count == 0
            ? ["No profiles found."]
            : profiles.Select(profile => EngineText.StripEnd($"- {profile.Name} {profile.Description ?? string.Empty}")).ToList();
    }

    public static OrderedDictionary<string, object?> Show(string name, string? baseDir) => RunProfileStore.LoadProfile(name, baseDir).ToDict();

    /// <summary>
    /// The profile from <paramref name="profilePath"/> or by <paramref name="name"/>, with secret overrides applied, saved when
    /// <paramref name="save"/> is set, then executed.
    /// </summary>
    public static ProfileRunResult Run(
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
            ? RunProfile.FromDict(RunProfileStore.ReadJson(profilePath))
            : RunProfileStore.LoadProfile(name ?? throw new ArgumentNullException(nameof(name)), baseDir);
        profile = ApplySecretOverrides(profile, ignoreRules, ignorePatterns);
        if (save)
        {
            RunProfileStore.SaveProfile(profile, baseDir);
        }

        return RunProfileExecutor.ExecuteProfile(profile, baseDir, timestamp, cancellationToken);
    }
}
