using System.Collections;
using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Profiles.Run;

public sealed partial class RunProfile
{
    private static readonly string[] UnsupportedSourceKeys = ["registry_scan", "sql_snapshot"];

    /// <summary>
    /// True when <paramref name="payload"/> is a mapping whose <c>sources</c> list holds a structured source: a mapping naming a
    /// <c>registry_scan</c> or <c>sql_snapshot</c>, one <see cref="SourceFromDict"/> refuses, or one that sets an alias, optional or
    /// exclude patterns. A mapping holding only a path is a string source.
    /// </summary>
    public static bool IsStructuredPayload(object? payload)
        => payload is IReadOnlyDictionary<string, object?> mapping
            && mapping.TryGetValue("sources", out var sources)
            && sources is IList list and not IReadOnlyDictionary<string, object?>
            && list.Cast<object?>().Any(IsStructuredEntry);

    private static bool IsStructuredEntry(object? entry)
    {
        if (entry is not IReadOnlyDictionary<string, object?> mapping)
        {
            return false;
        }

        if (UnsupportedSourceKeys.Any(mapping.ContainsKey))
        {
            return true;
        }

        try
        {
            return !SourceFromDict(mapping).IsPathOnly;
        }
        catch (Exception exc) when (exc is ArgumentException or PythonAttributeException)
        {
            return true;
        }
    }

    /// <summary>
    /// <c>OfflineRunnerProfile.from_dict(payload)</c> for a profile with a structured source (plan decision: structured sources follow the
    /// offline runner): a non-blank <c>name</c>, a non-empty <c>sources</c>, each mapping read with <see cref="SourceFromDict"/> and each
    /// other entry as the path <c>str(entry)</c>, a <c>baseline</c> (when not null) that is one of the source paths, <c>tags</c> iterated,
    /// <c>options</c> a mapping, a truthy <c>secret_scanner</c> a mapping, and <c>description</c> as <c>str()</c>; the values are then held
    /// as <c>run_profiles</c> holds them. A <c>registry_scan</c> or <c>sql_snapshot</c> source is not a run profile source and is refused.
    /// </summary>
    /// <exception cref="PythonValueException">Python's <c>ValueError</c> for each check, or an unsupported source.</exception>
    internal static RunProfile FromOfflineRunnerDict(IReadOnlyDictionary<string, object?> payload)
    {
        var name = payload.GetValueOrDefault("name");
        if (!PythonBuiltins.IsTruthy(name) || PythonText.Strip(PythonRepr.Str(name)).Length == 0)
        {
            throw new PythonValueException("Profile requires a non-empty 'name'.", nameof(payload));
        }

        var rawSources = payload.TryGetValue("sources", out var sourcesValue) ? sourcesValue : new List<object?>();
        if (!PythonBuiltins.IsTruthy(rawSources))
        {
            throw new PythonValueException("Profile must define at least one source.", nameof(payload));
        }

        var sources = PythonBuiltins.Iterate(rawSources).Select(OfflineRunnerSource).ToList();
        var baseline = payload.GetValueOrDefault("baseline");
        string? baselineText = null;
        if (baseline is not null)
        {
            baselineText = PythonRepr.Str(baseline);
            if (!sources.Any(source => string.Equals(source.Path, baselineText, StringComparison.Ordinal)))
            {
                throw new PythonValueException("Profile baseline must reference one of the declared sources.", nameof(payload));
            }
        }

        var tags = payload.GetValueOrDefault("tags");
        if (tags is not string && PythonBuiltins.IsTruthy(tags))
        {
            _ = PythonBuiltins.Iterate(tags);
        }

        var options = payload.TryGetValue("options", out var optionsValue) ? optionsValue : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (options is not IReadOnlyDictionary<string, object?> optionsMapping)
        {
            throw new PythonValueException("Profile 'options' must be a mapping if provided.", nameof(payload));
        }

        var secretScanner = payload.GetValueOrDefault("secret_scanner");
        if (PythonBuiltins.IsTruthy(secretScanner) && secretScanner is not IReadOnlyDictionary<string, object?>)
        {
            throw new PythonValueException("Profile 'secret_scanner' must be a mapping if provided.", nameof(payload));
        }

        return new RunProfile(
            PythonRepr.Str(name),
            DetectionProfileStore.OptionalText(payload.GetValueOrDefault("description")),
            sources,
            baselineText,
            optionsMapping,
            secretScanner as IReadOnlyDictionary<string, object?>)
        {
            SecretOptions = new ReadOnlyDictionary<string, object?>(new OrderedDictionary<string, object?>(optionsMapping, StringComparer.Ordinal)),
        };
    }

    // One entry of an offline runner profile's sources: a mapping through SourceFromDict (a registry scan or SQL snapshot refused), any
    // other value as OfflineCollectionSource.from_dict({"path": str(entry)}).
    private static RunProfileSource OfflineRunnerSource(object? entry)
    {
        if (entry is IReadOnlyDictionary<string, object?> mapping)
        {
            if (UnsupportedSourceKeys.FirstOrDefault(mapping.ContainsKey) is { } key)
            {
                throw new PythonValueException($"Run profiles do not support '{key}' sources.", nameof(entry));
            }

            return SourceFromDict(mapping);
        }

        return SourceFromDict(new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = PythonRepr.Str(entry) });
    }
}
