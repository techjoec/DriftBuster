using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// A run profile: name, description, sources, baseline source path, options (stored as text, null as "") and the secret scanner
/// mapping (<c>ignore_rules</c>, <c>ignore_patterns</c>, an optional <c>ruleset</c>). Values are <see cref="EngineJson"/> values.
/// </summary>
/// <remarks>
/// A string source entry is a path-only <see cref="RunProfileSource"/>; an object entry is read by <see cref="SourceFromDict"/>.
/// <see cref="ToDict"/> writes path-only sources back as strings.
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
    /// The options the secret filter reads: <see cref="Options"/>, except for a structured profile, whose option values stay as the
    /// payload holds them (a list stays a list, so each item is a pattern).
    /// </summary>
    internal IReadOnlyDictionary<string, object?> SecretOptions { get; private set; }

    /// <summary>A copy holding <paramref name="secretOptions"/>, for a profile read back from another's <see cref="ToDict"/>.</summary>
    internal RunProfile WithSecretOptions(IReadOnlyDictionary<string, object?> secretOptions)
    {
        ArgumentNullException.ThrowIfNull(secretOptions);
        SecretOptions = secretOptions;
        return this;
    }

    public IReadOnlyDictionary<string, object?> SecretScanner { get; }

    /// <summary>True when any source sets an alias, optional or exclude; the whole profile is then collected like the offline runner.</summary>
    public bool IsStructured => Sources.Any(source => !source.IsPathOnly);

    /// <summary>
    /// Reads a profile payload: <c>name</c> required (<see cref="KeyNotFoundException"/>), others optional. A payload with a
    /// structured source (<see cref="IsStructuredPayload"/>) is read by <see cref="FromOfflineRunnerDict"/>.
    /// </summary>
    public static RunProfile FromDict(object? payload)
    {
        if (IsStructuredPayload(payload))
        {
            return FromOfflineRunnerDict((IReadOnlyDictionary<string, object?>)payload!);
        }

        var name = EngineRepr.Str(DetectionProfileStore.Subscript(payload, "name"));
        var description = DetectionProfileStore.GetOrDefault(payload, "description", null);
        var sources = EngineBuiltins.Iterate(DetectionProfileStore.GetOrDefault(payload, "sources", new List<object?>())).Select(SourceFromEntry).ToList();
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
    /// A structured source: non-empty <c>path</c> (<see cref="InvalidDataException"/> otherwise), <c>alias</c> dropped when blank,
    /// <c>optional</c> as a bool, <c>exclude</c> as one pattern or a list.
    /// </summary>
    public static RunProfileSource SourceFromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var path = payload.GetValueOrDefault("path");
        if (!EngineBuiltins.IsTruthy(path) || EngineText.Strip(EngineRepr.Str(path)).Length == 0)
        {
            throw new InvalidDataException("Source entry requires a non-empty 'path'.");
        }

        var alias = payload.GetValueOrDefault("alias");
        if (alias is not null && EngineText.Strip(EngineRepr.Str(alias)).Length == 0)
        {
            alias = null;
        }

        var exclude = payload.TryGetValue("exclude", out var excludePayload) ? excludePayload : new List<object?>();
        string[] patterns = exclude is string single
            ? [single]
            : EngineBuiltins.IsTruthy(exclude) ? EngineBuiltins.Iterate(exclude).Select(EngineRepr.Str).ToArray() : [];
        return new RunProfileSource(EngineRepr.Str(path))
        {
            Alias = EngineBuiltins.IsTruthy(alias) ? EngineRepr.Str(alias) : null,
            Optional = EngineBuiltins.IsTruthy(payload.GetValueOrDefault("optional", false)),
            Exclude = patterns,
        };
    }

    /// <summary>A source for <see cref="ToDict"/>: the path alone as a string, otherwise the set keys.</summary>
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

    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["name"] = Name,
        ["description"] = Description,
        ["sources"] = Sources.Select(SourceToDict).Cast<object?>().ToList(),
        ["baseline"] = Baseline,
        ["options"] = new OrderedDictionary<string, object?>(Options.Select(pair => KeyValuePair.Create(pair.Key, (object?)pair.Value)), StringComparer.Ordinal),
        ["secret_scanner"] = SerialiseSecretScanner(SecretScanner),
    };

    /// <summary>The GUI model as a profile: an empty baseline is none, and only non-empty ignore lists are kept.</summary>
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
        => SecretScanner.TryGetValue(key, out var values) && values is List<object?> list ? list.Select(EngineRepr.Str).ToArray() : [];

    // A blank alias is dropped, as SourceFromDict drops it, so a reload names the same directory.
    private static RunProfileSource CopySource(RunProfileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RunProfileSource(source.Path)
        {
            Alias = source.Alias is { } alias && EngineText.Strip(alias).Length > 0 ? alias : null,
            Optional = source.Optional,
            Exclude = source.Exclude is null ? [] : [.. source.Exclude],
        };
    }

    // A mapping entry is a structured source; anything else is its text as a path.
    private static RunProfileSource SourceFromEntry(object? entry)
        => entry is IReadOnlyDictionary<string, object?> mapping ? SourceFromDict(mapping) : new RunProfileSource(EngineRepr.Str(entry));

    // Falsy is empty, a dict is itself, anything else is refused.
    private static IReadOnlyDictionary<string, object?>? Mapping(object? value)
    {
        if (!EngineBuiltins.IsTruthy(value))
        {
            return null;
        }

        return value as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidDataException($"expected a JSON object, not '{EngineBuiltins.TypeName(value)}'");
    }

    private static ReadOnlyDictionary<string, string> NormaliseOptions(IReadOnlyDictionary<string, object?>? options)
    {
        var normalised = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in options ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal))
        {
            normalised[key] = value is null ? string.Empty : EngineRepr.Str(value);
        }

        return new ReadOnlyDictionary<string, string>(normalised);
    }

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
