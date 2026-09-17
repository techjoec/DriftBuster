using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Secrets;

/// <summary><c>build_context</c> and <c>manifest_secret_scanner</c>.</summary>
public static partial class SecretScanner
{
    /// <summary>
    /// <c>build_context(options, secret_scanner)</c>: an inline <c>secret_scanner.ruleset</c> mapping that compiles wins over
    /// the packaged rules; ignore rules are the union of <c>options.secret_ignore_rules</c> and
    /// <c>secret_scanner.ignore_rules</c>; ignore patterns are <c>options.secret_ignore_patterns</c> then
    /// <c>secret_scanner.ignore_patterns</c>, deduplicated in order and compiled without flags (a pattern that does not
    /// compile is kept in the text list and dropped from the compiled one).
    /// </summary>
    public static SecretDetectionContext BuildContext(IReadOnlyDictionary<string, object?>? options, IReadOnlyDictionary<string, object?>? secretScanner)
    {
        object? rulesetPayload = null;
        if (IsTruthy(secretScanner))
        {
            rulesetPayload = Get(secretScanner!, "ruleset");
        }

        IReadOnlyList<SecretDetectionRule> rules;
        string version;
        bool loaded;
        if (CompileRulesetFromMapping(rulesetPayload as IReadOnlyDictionary<string, object?>) is { } inline)
        {
            (rules, version, loaded) = (inline.Rules, inline.Version, inline.Rules.Count > 0);
        }
        else
        {
            (rules, version, loaded) = LoadSecretRules();
        }

        var ignoreRules = new HashSet<string>(StringComparer.Ordinal);
        var patternSources = new List<string>();
        if (IsTruthy(options))
        {
            ignoreRules.UnionWith(SecretOptionValues(Get(options!, "secret_ignore_rules")));
            patternSources.AddRange(SecretOptionValues(Get(options!, "secret_ignore_patterns")));
        }

        if (IsTruthy(secretScanner))
        {
            ignoreRules.UnionWith(SecretOptionValues(Get(secretScanner!, "ignore_rules")));
            patternSources.AddRange(SecretOptionValues(Get(secretScanner!, "ignore_patterns")));
        }

        var patternText = new List<string>();
        var patterns = new List<EnginePattern>();
        foreach (var source in patternSources.Distinct(StringComparer.Ordinal))
        {
            patternText.Add(source);
            try
            {
                patterns.Add(EnginePattern.Compile(source));
            }
            catch (EngineReException)
            {
                // re.error: the text stays listed, the pattern is not applied.
            }
        }

        return new SecretDetectionContext(rules, version, ignoreRules, patterns, patternText, loaded && rules.Count > 0);
    }

    /// <summary>
    /// The <c>secret_metadata</c> mapping <c>run_profiles.execute_profile</c> builds after copying a run's files: the ruleset
    /// version, whether rules were loaded, the ignore rules (sorted) and ignore pattern texts, every finding, and the log
    /// messages when there are any. It is written under <c>secrets</c> in the run's <c>metadata.json</c> and returned as
    /// <c>ProfileRunResult.secrets</c>.
    /// </summary>
    public static OrderedDictionary<string, object?> RunSecretsMetadata(SecretDetectionContext context, IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);
        var ignoredRules = context.IgnoreRules.ToList();
        ignoredRules.Sort(PathText.CompareCodePoints);
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ruleset_version"] = context.Version,
            ["rules_loaded"] = context.Rules.Count > 0 && context.RulesLoaded,
            ["ignored_rules"] = ignoredRules.Cast<object?>().ToList(),
            ["ignored_patterns"] = context.IgnorePatternText.Cast<object?>().ToList(),
            ["findings"] = context.Findings.Select(finding => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = finding.Path,
                ["rule"] = finding.Rule,
                ["line"] = finding.Line,
                ["snippet"] = finding.Snippet,
            }).ToList(),
        };
        if (messages.Count > 0)
        {
            metadata["messages"] = messages.Cast<object?>().ToList();
        }

        return metadata;
    }

    /// <summary>
    /// <c>manifest_secret_scanner(options, secret_scanner, context)</c>: the ignore lists as sorted, distinct values and the
    /// ruleset version.
    /// </summary>
    public static OrderedDictionary<string, object?> ManifestSecretScanner(
        IReadOnlyDictionary<string, object?> options,
        IReadOnlyDictionary<string, object?> secretScanner,
        SecretDetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretScanner);
        ArgumentNullException.ThrowIfNull(context);
        static List<object?> Sorted(IEnumerable<string> values)
        {
            var distinct = values.Distinct(StringComparer.Ordinal).ToList();
            distinct.Sort(PathText.CompareCodePoints);
            return distinct.Cast<object?>().ToList();
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ignore_rules"] = Sorted(SecretOptionValues(Get(options, "secret_ignore_rules")).Concat(SecretOptionValues(Get(secretScanner, "ignore_rules")))),
            ["ignore_patterns"] = Sorted(SecretOptionValues(Get(options, "secret_ignore_patterns")).Concat(SecretOptionValues(Get(secretScanner, "ignore_patterns")))),
            ["ruleset_version"] = context.Version,
        };
    }
}
