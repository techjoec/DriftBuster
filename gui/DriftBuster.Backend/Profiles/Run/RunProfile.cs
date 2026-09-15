using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// <c>run_profiles.RunProfile</c>: a name, a description, the sources, the baseline source path, the options (every value
/// <c>str()</c>-ed, <c>None</c> as "") and the secret scanner mapping (<c>ignore_rules</c> and <c>ignore_patterns</c> read with
/// <c>secret_option_values</c>, a <c>ruleset</c> mapping copied, anything else kept). Values are in the <see cref="PythonJson"/>
/// domain.
/// </summary>
/// <remarks>
/// Plan decision, structured sources: a source is a <see cref="RunProfileSource"/>. A string entry is a source with only its path and
/// behaves exactly as Python's string source; an object entry is read as <c>OfflineCollectionSource.from_dict</c> reads it
/// (<see cref="SourceFromDict"/>), and <see cref="ToDict"/> writes a path-only source back as a string.
/// </remarks>
public sealed partial class RunProfile
{
    private static readonly string[] IgnoreKeys = ["ignore_rules", "ignore_patterns"];

    public RunProfile(
        string name,
        string? description = null,
        IEnumerable<RunProfileSource>? sources = null,
        string? baseline = null,
        IReadOnlyDictionary<string, object?>? options = null,
        IReadOnlyDictionary<string, object?>? secretScanner = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Description = description;
        Baseline = baseline;
        Sources = (sources ?? []).Select(CopySource).ToList().AsReadOnly();
        Options = NormaliseOptions(options);
        SecretOptions = new ReadOnlyDictionary<string, object?>(Options.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal));
        SecretScanner = NormaliseSecretScanner(secretScanner);
    }

    public string Name { get; }

    public string? Description { get; }

    public IReadOnlyList<RunProfileSource> Sources { get; }

    public string? Baseline { get; }

    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>
    /// The options a run hands <c>build_context</c>: <see cref="Options"/>, except for a profile read with
    /// <c>OfflineRunnerProfile.from_dict</c>, where <c>execute_config</c> hands it the values as the payload holds them (a number, bool or
    /// list is not <c>str()</c>-ed, so <c>secret_option_values</c> reads a list's items and no pattern from a scalar).
    /// </summary>
    internal IReadOnlyDictionary<string, object?> SecretOptions { get; private set; }

    /// <summary>
    /// This profile holding <paramref name="secretOptions"/> as its <see cref="SecretOptions"/>, for a profile read back from another's
    /// <see cref="ToDict"/>, whose options are <c>str()</c> text there.
    /// </summary>
    internal RunProfile WithSecretOptions(IReadOnlyDictionary<string, object?> secretOptions)
    {
        ArgumentNullException.ThrowIfNull(secretOptions);
        SecretOptions = secretOptions;
        return this;
    }

    public IReadOnlyDictionary<string, object?> SecretScanner { get; }

    /// <summary>
    /// True when a source sets an alias, optional or exclude patterns: every source of such a profile is collected and validated as the
    /// offline runner collects it (plan decision, structured sources).
    /// </summary>
    public bool IsStructured => Sources.Any(source => !source.IsPathOnly);

    /// <summary>
    /// <c>RunProfile.from_dict(payload)</c>: <c>payload["name"]</c> (<see cref="KeyNotFoundException"/> when absent), and
    /// <c>description</c>, <c>sources</c>, <c>baseline</c>, <c>options</c> and <c>secret_scanner</c> when present. A payload holding a
    /// structured source (<see cref="IsStructuredPayload"/>) is read as <c>OfflineRunnerProfile.from_dict</c> reads it instead
    /// (<see cref="FromOfflineRunnerDict"/>).
    /// </summary>
    public static RunProfile FromDict(object? payload)
    {
        if (IsStructuredPayload(payload))
        {
            return FromOfflineRunnerDict((IReadOnlyDictionary<string, object?>)payload!);
        }

        var name = PythonRepr.Str(DetectionProfileStore.Subscript(payload, "name"));
        var description = DetectionProfileStore.GetOrDefault(payload, "description", null);
        var sources = PythonBuiltins.Iterate(DetectionProfileStore.GetOrDefault(payload, "sources", new List<object?>())).Select(SourceFromEntry).ToList();
        var baseline = DetectionProfileStore.GetOrDefault(payload, "baseline", null);
        var options = Mapping(DetectionProfileStore.GetOrDefault(payload, "options", null));
        var secretScanner = Mapping(DetectionProfileStore.GetOrDefault(payload, "secret_scanner", null));
        return new RunProfile(
            name,
            DetectionProfileStore.OptionalText(description),
            sources,
            DetectionProfileStore.OptionalText(baseline),
            options,
            secretScanner);
    }

    /// <summary>
    /// <c>OfflineCollectionSource.from_dict(payload)</c>: a non-empty <c>path</c> (<c>ValueError</c> otherwise), an <c>alias</c> that
    /// is dropped when blank or falsy, <c>bool(optional)</c>, and <c>exclude</c> as one pattern for a str or each item's <c>str()</c>.
    /// </summary>
    public static RunProfileSource SourceFromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var path = payload.GetValueOrDefault("path");
        if (!PythonBuiltins.IsTruthy(path) || PythonText.Strip(PythonRepr.Str(path)).Length == 0)
        {
            throw new PythonValueException("Source entry requires a non-empty 'path'.", nameof(payload));
        }

        var alias = payload.GetValueOrDefault("alias");
        if (alias is not null && PythonText.Strip(PythonRepr.Str(alias)).Length == 0)
        {
            alias = null;
        }

        var exclude = payload.TryGetValue("exclude", out var excludePayload) ? excludePayload : new List<object?>();
        string[] patterns = exclude is string single
            ? [single]
            : PythonBuiltins.IsTruthy(exclude) ? PythonBuiltins.Iterate(exclude).Select(PythonRepr.Str).ToArray() : [];
        return new RunProfileSource(PythonRepr.Str(path))
        {
            Alias = PythonBuiltins.IsTruthy(alias) ? PythonRepr.Str(alias) : null,
            Optional = PythonBuiltins.IsTruthy(payload.GetValueOrDefault("optional", false)),
            Exclude = patterns,
        };
    }

    /// <summary>A source as <see cref="ToDict"/> writes it: the path alone as a str, otherwise a mapping of the keys that are set.</summary>
    public static object SourceToDict(RunProfileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsPathOnly)
        {
            return source.Path;
        }

        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = source.Path };
        if (source.Alias is not null)
        {
            entry["alias"] = source.Alias;
        }

        if (source.Optional)
        {
            entry["optional"] = true;
        }

        if (source.Exclude is { Length: > 0 })
        {
            entry["exclude"] = source.Exclude.Cast<object?>().ToList();
        }

        return entry;
    }

    /// <summary><c>RunProfile.to_dict()</c>.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["name"] = Name,
        ["description"] = Description,
        ["sources"] = Sources.Select(SourceToDict).Cast<object?>().ToList(),
        ["baseline"] = Baseline,
        ["options"] = new OrderedDictionary<string, object?>(Options.Select(pair => KeyValuePair.Create(pair.Key, (object?)pair.Value)), StringComparer.Ordinal),
        ["secret_scanner"] = SerialiseSecretScanner(SecretScanner),
    };

    /// <summary>The GUI model as a profile: an empty baseline is no baseline, and the secret scanner holds each ignore list that is not empty.</summary>
    public static RunProfile FromDefinition(RunProfileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in definition.Options ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            options[key] = value;
        }

        var secretScanner = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (definition.SecretScanner?.IgnoreRules is { Length: > 0 } rules)
        {
            secretScanner["ignore_rules"] = rules.Cast<object?>().ToList();
        }

        if (definition.SecretScanner?.IgnorePatterns is { Length: > 0 } patterns)
        {
            secretScanner["ignore_patterns"] = patterns.Cast<object?>().ToList();
        }

        var baseline = string.IsNullOrEmpty(definition.Baseline) ? null : definition.Baseline;
        return new RunProfile(definition.Name, definition.Description, definition.Sources ?? [], baseline, options, secretScanner);
    }

    /// <summary>The profile as the GUI model.</summary>
    public RunProfileDefinition ToDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Sources = Sources.Select(CopySource).ToArray(),
        Baseline = Baseline,
        Options = new Dictionary<string, string>(Options, StringComparer.Ordinal),
        SecretScanner = new SecretScannerOptions
        {
            IgnoreRules = IgnoreValues("ignore_rules"),
            IgnorePatterns = IgnoreValues("ignore_patterns"),
        },
    };

    private string[] IgnoreValues(string key)
        => SecretScanner.TryGetValue(key, out var values) && values is List<object?> list ? list.Select(PythonRepr.Str).ToArray() : [];

    // A copy of a model source; a blank alias is dropped, as SourceFromDict drops it, so the directory name and profile.json agree with a
    // reload.
    private static RunProfileSource CopySource(RunProfileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RunProfileSource(source.Path)
        {
            Alias = source.Alias is { } alias && PythonText.Strip(alias).Length > 0 ? alias : null,
            Optional = source.Optional,
            Exclude = source.Exclude is null ? [] : [.. source.Exclude],
        };
    }

    // An entry of payload["sources"]: a mapping is a structured source, anything else str()-ed into a path.
    private static RunProfileSource SourceFromEntry(object? entry)
        => entry is IReadOnlyDictionary<string, object?> mapping ? SourceFromDict(mapping) : new RunProfileSource(PythonRepr.Str(entry));

    // The argument _normalise_options and _normalise_secret_scanner call .items() on: falsy is empty, a dict is itself, anything else
    // raises AttributeError.
    private static IReadOnlyDictionary<string, object?>? Mapping(object? value)
    {
        if (!PythonBuiltins.IsTruthy(value))
        {
            return null;
        }

        return value as IReadOnlyDictionary<string, object?>
            ?? throw new PythonAttributeException($"'{PythonBuiltins.TypeName(value)}' object has no attribute 'items'");
    }

    // _normalise_options.
    private static ReadOnlyDictionary<string, string> NormaliseOptions(IReadOnlyDictionary<string, object?>? options)
    {
        var normalised = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in options ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal))
        {
            normalised[key] = value is null ? string.Empty : PythonRepr.Str(value);
        }

        return new ReadOnlyDictionary<string, string>(normalised);
    }

    // _normalise_secret_scanner.
    private static ReadOnlyDictionary<string, object?> NormaliseSecretScanner(IReadOnlyDictionary<string, object?>? secretScanner)
    {
        var normalised = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in secretScanner ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal))
        {
            if (IgnoreKeys.Contains(key, StringComparer.Ordinal))
            {
                normalised[key] = Secrets.SecretScanner.SecretOptionValues(value).Cast<object?>().ToList();
            }
            else if (string.Equals(key, "ruleset", StringComparison.Ordinal) && value is IReadOnlyDictionary<string, object?> ruleset)
            {
                normalised[key] = new OrderedDictionary<string, object?>(ruleset, StringComparer.Ordinal);
            }
            else
            {
                normalised[key] = value;
            }
        }

        return new ReadOnlyDictionary<string, object?>(normalised);
    }

    // _serialise_secret_scanner.
    private static OrderedDictionary<string, object?> SerialiseSecretScanner(IReadOnlyDictionary<string, object?> secretScanner)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in secretScanner)
        {
            payload[key] = IgnoreKeys.Contains(key, StringComparer.Ordinal) && value is List<object?> list ? new List<object?>(list) : value;
        }

        return payload;
    }
}
