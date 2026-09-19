using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Secrets;

/// <summary>
/// Secret rule compilation and loading, ignore lists, and the redacting copy. Mappings and values are <see cref="EngineJson"/> values.
/// </summary>
public static partial class SecretScanner
{
    /// <summary>The packaged ruleset, embedded from <c>Resources/secret_rules.json</c>.</summary>
    public const string SecretRulesResource = "DriftBuster.Backend.Resources.secret_rules.json";

    private static readonly Lock CacheLock = new();

    /// <summary>The loaded rules; null until loaded.</summary>
    internal static IReadOnlyList<SecretDetectionRule>? RuleCache { get; set; }

    /// <summary>The loaded ruleset version; null until loaded.</summary>
    internal static string? RuleVersion { get; set; }

    /// <summary>Whether a ruleset was loaded; null until loaded.</summary>
    internal static bool? RuleLoaded { get; set; }

    /// <summary>Reads the packaged ruleset text, or null when it is not available.</summary>
    internal static Func<string?> ResourceReader { get; set; } = ReadEmbeddedRules;

    public static void ResetSecretRuleCache()
    {
        lock (CacheLock)
        {
            RuleCache = null;
            RuleVersion = null;
            RuleLoaded = null;
        }
    }

    /// <summary>
    /// Null for a falsy or non-mapping payload, a <c>rules</c> value that is not a sequence, or no usable rule. Entries without a name or
    /// pattern are skipped, <c>flags</c> honours only <c>i</c>, and a pattern that does not compile is skipped silently.
    /// </summary>
    public static SecretRuleset? CompileRulesetFromMapping(object? payload)
    {
        if (!IsTruthy(payload) || payload is not IReadOnlyDictionary<string, object?> mapping)
        {
            return null;
        }

        if (!mapping.TryGetValue("rules", out var rulesPayload) || !IsSequence(rulesPayload))
        {
            return null;
        }

        var compiled = new List<SecretDetectionRule>();
        foreach (var entry in SequenceItems(rulesPayload!))
        {
            if (entry is IReadOnlyDictionary<string, object?> rule && CompileRule(rule) is { } compiledRule)
            {
                compiled.Add(compiledRule);
            }
        }

        if (compiled.Count == 0)
        {
            return null;
        }

        return new SecretRuleset(compiled, EngineRepr.Str(OrElse(Get(mapping, "version"), string.Empty)));
    }

    private static SecretDetectionRule? CompileRule(IReadOnlyDictionary<string, object?> entry)
    {
        var name = EngineText.Strip(EngineRepr.Str(OrElse(Get(entry, "name"), string.Empty)));
        var patternText = Get(entry, "pattern");
        if (name.Length == 0 || !IsTruthy(patternText))
        {
            return null;
        }

        var flagsText = EngineText.Lower(EngineRepr.Str(OrElse(Get(entry, "flags"), string.Empty)));
        var options = flagsText.Contains('i', StringComparison.Ordinal) ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex pattern;
        try
        {
            pattern = PatternRegex.Create(EngineRepr.Str(patternText), options);
        }
        catch (RegexParseException)
        {
            return null;
        }

        var description = Get(entry, "description");
        return new SecretDetectionRule(name, pattern, IsTruthy(description) ? EngineRepr.Str(description) : null);
    }

    /// <summary>
    /// The cached rules, version and loaded flag, loading the packaged ruleset on first use. A missing resource or a JSON <c>null</c>
    /// caches no rules, version "none", not loaded; a ruleset without usable rules caches no rules with its version (or "unknown"),
    /// loaded. A payload that is not a mapping raises <see cref="InvalidOperationException"/>.
    /// </summary>
    public static (IReadOnlyList<SecretDetectionRule> Rules, string Version, bool Loaded) LoadSecretRules()
    {
        lock (CacheLock)
        {
            if (RuleCache is not null && RuleVersion is not null && RuleLoaded is not null)
            {
                return (RuleCache, RuleVersion, RuleLoaded.Value);
            }

            var text = ResourceReader();
            object? payload = null;
            if (text is not null && !EngineJson.TryLoads(text, out payload))
            {
                throw new InvalidDataException("secret rules resource is not valid JSON");
            }

            if (payload is null)
            {
                (RuleCache, RuleVersion, RuleLoaded) = ([], "none", false);
            }
            else if (CompileRulesetFromMapping(payload) is { } compiled)
            {
                var version = compiled.Version.Length > 0 ? compiled.Version : EngineRepr.Str(PayloadVersion(payload));
                (RuleCache, RuleVersion, RuleLoaded) = (compiled.Rules, version, true);
            }
            else
            {
                (RuleCache, RuleVersion, RuleLoaded) = ([], EngineRepr.Str(PayloadVersion(payload)), true);
            }

            return (RuleCache, RuleVersion, RuleLoaded.Value);
        }
    }

    private static string? ReadEmbeddedRules()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SecretRulesResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A string split on runs of whitespace, commas and semicolons; a sequence's non-null items as stripped strings; empties dropped;
    /// anything else (a mapping, a number) gives nothing.
    /// </summary>
    public static IReadOnlyList<string> SecretOptionValues(object? value)
    {
        if (!IsTruthy(value))
        {
            return [];
        }

        if (value is string text)
        {
            return SplitOptionText(text);
        }

        if (!IsSequence(value))
        {
            return [];
        }

        return SequenceItems(value!)
            .Where(item => item is not null)
            .Select(item => EngineText.Strip(EngineRepr.Str(item)))
            .Where(item => item.Length > 0)
            .ToList();
    }

    // Split on runs of whitespace, commas and semicolons, empty parts dropped and the rest stripped.
    private static List<string> SplitOptionText(string text)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && !EngineText.IsSpace(text[index]) && text[index] is not (',' or ';'))
            {
                continue;
            }

            var part = EngineText.Strip(text[start..index]);
            if (part.Length > 0)
            {
                parts.Add(part);
            }

            start = index + 1;
        }

        return parts;
    }

    /// <summary>Truthiness over the JSON value domain: null, false, zero and empty strings and containers are false.</summary>
    internal static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int number => number != 0,
        long number => number != 0,
        BigInteger number => !number.IsZero,
        double number => number != 0.0,
        ICollection collection => collection.Count > 0,
        IEnumerable sequence => sequence.Cast<object?>().Any(),
        _ => true,
    };

    // A string, list or tuple, never a mapping.
    private static bool IsSequence(object? value) => value is string || (value is IEnumerable && value is not IDictionary && value is not IReadOnlyDictionary<string, object?>);

    private static IEnumerable<object?> SequenceItems(object value)
        => value is string text ? text.EnumerateRunes().Select(rune => (object?)rune.ToString()) : ((IEnumerable)value).Cast<object?>();

    private static object? Get(IReadOnlyDictionary<string, object?> mapping, string key) => mapping.TryGetValue(key, out var value) ? value : null;

    // The payload's "version", else "unknown".
    private static object? PayloadVersion(object payload)
    {
        if (payload is IReadOnlyDictionary<string, object?> mapping)
        {
            return mapping.TryGetValue("version", out var value) ? value : "unknown";
        }

        throw new InvalidOperationException($"expected a JSON object, not '{EngineBuiltins.TypeName(payload)}'");
    }

    private static object? OrElse(object? value, object fallback) => IsTruthy(value) ? value : fallback;
}
