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
        catch (Exception exc) when (exc is ArgumentException or FormatException or InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads a profile with structured sources the way the offline runner reads its profile: non-blank <c>name</c>, non-empty
    /// <c>sources</c> (objects through <see cref="SourceFromDict"/>, other entries as paths), a <c>baseline</c> that is a source path,
    /// <c>options</c> and <c>secret_scanner</c> objects. <c>registry_scan</c> and <c>sql_snapshot</c> sources are refused.
    /// </summary>
    /// <exception cref="InvalidDataException">A failed check or an unsupported source.</exception>
    internal static RunProfile FromOfflineRunnerDict(IReadOnlyDictionary<string, object?> payload)
    {
        var name = payload.GetValueOrDefault("name");
        if (!EngineBuiltins.IsTruthy(name) || EngineText.Strip(EngineRepr.Str(name)).Length == 0)
        {
            throw new InvalidDataException("Profile requires a non-empty 'name'.");
        }

        var rawSources = payload.TryGetValue("sources", out var sourcesValue) ? sourcesValue : new List<object?>();
        if (!EngineBuiltins.IsTruthy(rawSources))
        {
            throw new InvalidDataException("Profile must define at least one source.");
        }

        var sources = EngineBuiltins.Iterate(rawSources).Select(OfflineRunnerSource).ToList();
        var baseline = payload.GetValueOrDefault("baseline");
        string? baselineText = null;
        if (baseline is not null)
        {
            baselineText = EngineRepr.Str(baseline);
            if (!sources.Any(source => string.Equals(source.Path, baselineText, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Profile baseline must reference one of the declared sources.");
            }
        }

        var tags = payload.GetValueOrDefault("tags");
        if (tags is not string && EngineBuiltins.IsTruthy(tags))
        {
            _ = EngineBuiltins.Iterate(tags);
        }

        var options = payload.TryGetValue("options", out var optionsValue) ? optionsValue : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (options is not IReadOnlyDictionary<string, object?> optionsMapping)
        {
            throw new InvalidDataException("Profile 'options' must be a mapping if provided.");
        }

        var secretScanner = payload.GetValueOrDefault("secret_scanner");
        if (EngineBuiltins.IsTruthy(secretScanner) && secretScanner is not IReadOnlyDictionary<string, object?>)
        {
            throw new InvalidDataException("Profile 'secret_scanner' must be a mapping if provided.");
        }

        return new RunProfile(
            EngineRepr.Str(name),
            DetectionProfileStore.OptionalText(payload.GetValueOrDefault("description")),
            sources,
            baselineText,
            optionsMapping,
            secretScanner as IReadOnlyDictionary<string, object?>)
        {
            SecretOptions = new ReadOnlyDictionary<string, object?>(new OrderedDictionary<string, object?>(optionsMapping, StringComparer.Ordinal)),
        };
    }

    // An object entry through SourceFromDict (registry scan and SQL snapshot refused); any other value as a path.
    private static RunProfileSource OfflineRunnerSource(object? entry)
    {
        if (entry is IReadOnlyDictionary<string, object?> mapping)
        {
            if (UnsupportedSourceKeys.FirstOrDefault(mapping.ContainsKey) is { } key)
            {
                throw new InvalidDataException($"Run profiles do not support '{key}' sources.");
            }

            return SourceFromDict(mapping);
        }

        return SourceFromDict(new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = EngineRepr.Str(entry) });
    }
}
