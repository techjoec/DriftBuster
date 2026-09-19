using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Secrets;

public static partial class SecretScanner
{
    /// <summary>
    /// The packaged rules with the profile's ignore lists: rule names to skip, and ignore patterns (distinct, in order) whose matches on
    /// the original line are never redacted. Patterns are validated when the profile is saved; one that does not compile is skipped.
    /// </summary>
    public static SecretDetectionContext BuildContext(SecretScannerOptions? options)
    {
        var (rules, version) = SecretRules.Packaged;
        var patternText = (options?.IgnorePatterns ?? []).Distinct(StringComparer.Ordinal).ToList();
        var patterns = new List<Regex>();
        foreach (var source in patternText)
        {
            try
            {
                patterns.Add(PatternRegex.Create(source));
            }
            catch (RegexParseException)
            {
            }
        }

        var ignoreRules = new HashSet<string>(options?.IgnoreRules ?? [], StringComparer.Ordinal);
        return new SecretDetectionContext(rules, version, ignoreRules, patterns, patternText, rules.Count > 0);
    }

    /// <summary>What the filter did during a run, for the run result and <c>metadata.json</c>.</summary>
    public static SecretRunSummary Summarise(SecretDetectionContext context, IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);
        return new SecretRunSummary(
            context.Version,
            context.RulesLoaded,
            [.. context.IgnoreRules.Order(StringComparer.Ordinal)],
            context.IgnorePatternText,
            [.. context.Findings.Select(finding => new SecretFindingResult(finding.Path, finding.Rule, finding.Line, finding.Snippet))],
            [.. context.RedactionGuards.Select(guard => new SecretFindingResult(guard.Path, guard.Rule, guard.Line, string.Empty))],
            [.. messages]);
    }
}
