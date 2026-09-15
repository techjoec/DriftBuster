using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// The profile half of <c>run_profiles_cli</c> (<c>driftbuster-run</c>): <c>--option</c> and secret ignore argument handling, and the
/// <c>create</c>, <c>list</c>, <c>show</c> and <c>run</c> commands as library calls. Argument parsing, printing and exit codes belong to the
/// console tool.
/// </summary>
public static class RunProfileCommands
{
    /// <summary><c>_parse_options(pairs)</c>: <c>key=value</c> split at the first "=", both sides stripped; a pair without "=" raises <see cref="CommandExitException"/>.</summary>
    public static OrderedDictionary<string, object?> ParseOptions(IEnumerable<string> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in pairs)
        {
            var separator = item.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new CommandExitException($"Invalid option format: {PythonRepr.StrRepr(item)}. Use key=value.");
            }

            options[PythonText.Strip(item[..separator])] = PythonText.Strip(item[(separator + 1)..]);
        }

        return options;
    }

    /// <summary><c>_clean_secret_values(values)</c>: stripped values, blanks and repeats dropped, first occurrence order.</summary>
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

            var text = PythonText.Strip(value);
            if (text.Length > 0 && !cleaned.Contains(text, StringComparer.Ordinal))
            {
                cleaned.Add(text);
            }
        }

        return cleaned;
    }

    /// <summary><c>_build_secret_scanner_payload(rules, patterns)</c>: <c>ignore_rules</c> and <c>ignore_patterns</c>, each only when not empty after cleaning.</summary>
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
    /// <c>_apply_secret_overrides(profile, rules, patterns)</c>: the profile unchanged without overrides; otherwise its dict with each
    /// override list appended to the cleaned existing list (repeats skipped), read back through <see cref="RunProfile.FromDict"/>. The
    /// profile read back keeps the options <c>build_context</c> gets (<see cref="RunProfile.SecretOptions"/>): a structured profile's raw
    /// option values do not survive <see cref="RunProfile.ToDict"/>, which writes their <c>str()</c> text.
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

    /// <summary><c>_create(args)</c>: a profile from the arguments (string sources, parsed options, the secret scanner payload), saved; returns it.</summary>
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

    /// <summary><c>_list_profiles(args)</c>: the lines the command prints, "- name description" right-stripped, or "No profiles found.".</summary>
    public static IReadOnlyList<string> ListProfileLines(string? baseDir, CancellationToken cancellationToken = default)
    {
        var profiles = RunProfileStore.ListProfiles(baseDir, cancellationToken);
        return profiles.Count == 0
            ? ["No profiles found."]
            : profiles.Select(profile => PythonText.StripEnd($"- {profile.Name} {profile.Description ?? string.Empty}")).ToList();
    }

    /// <summary><c>_show(args)</c>: the saved profile's dict.</summary>
    public static OrderedDictionary<string, object?> Show(string name, string? baseDir) => RunProfileStore.LoadProfile(name, baseDir).ToDict();

    /// <summary>
    /// <c>_run(args)</c>: the profile read from <paramref name="profilePath"/> or loaded by <paramref name="name"/>, with the secret overrides
    /// applied, saved when <paramref name="save"/> is set, then executed.
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
