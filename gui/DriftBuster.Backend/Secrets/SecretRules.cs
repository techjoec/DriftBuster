using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Secrets;

/// <summary>The packaged secret rules (<c>Resources/secret_rules.json</c>), read strictly and compiled once.</summary>
public static class SecretRules
{
    public const string ResourceName = "DriftBuster.Backend.Resources.secret_rules.json";

    private static readonly Lazy<SecretRuleset> PackagedRules = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The {ResourceName} resource is missing from the build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static SecretRuleset Packaged => PackagedRules.Value;

    /// <summary>A ruleset file; <see cref="InvalidDataException"/> names the JSON path or the rule whose pattern does not compile.</summary>
    public static SecretRuleset Parse(string json)
    {
        SecretRuleFile file;
        try
        {
            file = JsonSerializer.Deserialize(json, ModelJson.TypeInfo<SecretRuleFile>()) ?? throw new InvalidDataException("The secret rules file holds null.");
        }
        catch (JsonException exc)
        {
            throw new InvalidDataException($"Secret rules: {exc.Path ?? "$"}: {exc.Message}", exc);
        }

        var rules = new List<SecretDetectionRule>(file.Rules.Count);
        foreach (var rule in file.Rules)
        {
            var options = rule.Flags.Contains('i', StringComparison.OrdinalIgnoreCase) ? RegexOptions.IgnoreCase : RegexOptions.None;
            try
            {
                rules.Add(new SecretDetectionRule(rule.Name, PatternRegex.Create(rule.Pattern, options), rule.Description));
            }
            catch (RegexParseException exc)
            {
                throw new InvalidDataException($"Secret rule '{rule.Name}': {exc.Message}", exc);
            }
        }

        return new SecretRuleset(rules, file.Version);
    }
}
