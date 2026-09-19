using System.Globalization;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Sensitive key hints, secret classification and remediation metadata.</summary>
public sealed partial class IniPlugin
{
    // Applied to the lowered ASCII key, so lowercase patterns act as case-insensitive.
    private static readonly (string Keyword, Regex Pattern)[] SensitiveKeyPatterns =
    [
        ("password", Sensitive("password")),
        ("passphrase", Sensitive("passphrase")),
        ("secret", Sensitive("secret")),
        ("token", Sensitive("token")),
        ("api-key", Sensitive("api[-_]?key")),
        ("client-id", Sensitive("client[-_]?id")),
        ("client-secret", Sensitive("client[-_]?secret")),
        ("private-key", Sensitive("private[-_]?key")),
        ("public-key", Sensitive("public[-_]?key")),
        ("access-key", Sensitive("access[-_]?key")),
        ("secret-key", Sensitive("secret[-_]?key")),
        ("credential", Sensitive("cred(ent|)ial")),
        ("auth", Sensitive("auth(ent|)")),
        ("key", Sensitive("(^|[^a-z0-9])key([^a-z0-9]|$)")),
    ];

    private static readonly Dictionary<string, string> SecretCategoryMap = new(StringComparer.Ordinal)
    {
        ["password"] = "credential",
        ["passphrase"] = "credential",
        ["secret"] = "credential",
        ["token"] = "token",
        ["api-key"] = "token",
        ["client-id"] = "client-identifier",
        ["client-secret"] = "credential",
        ["private-key"] = "key-material",
        ["public-key"] = "key-material",
        ["access-key"] = "credential",
        ["secret-key"] = "credential",
        ["credential"] = "credential",
        ["auth"] = "credential",
        ["key"] = "key-material",
    };

    private static Regex Sensitive(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    private static void CollectSensitiveHints(Scan scan, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        var sensitiveHints = new List<OrderedDictionary<string, object?>>();
        var seenSensitive = new HashSet<(string Key, string Keyword)>();
        foreach (var match in scan.KeyMatches)
        {
            var keyName = match.Groups["key"].Value;
            var keyLower = keyName.ToLowerInvariant();
            foreach (var (keyword, pattern) in SensitiveKeyPatterns)
            {
                if (!pattern.IsMatch(keyLower) || !seenSensitive.Add((keyName, keyword)))
                {
                    continue;
                }

                sensitiveHints.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["key"] = keyName,
                    ["keyword"] = keyword,
                });
                reasons.Add($"Sensitive key '{keyName}' matched keyword '{keyword}'");
            }
        }

        if (sensitiveHints.Count == 0)
        {
            return;
        }

        metadata["sensitive_key_hints"] = sensitiveHints;
        var (classification, remediations, classifiedKeys) = BuildSecretMetadata(sensitiveHints);
        metadata["secret_classification"] = classification;
        metadata["remediations"] = remediations;
        if (classifiedKeys.Count > 0)
        {
            metadata.TryAdd("security_focus_keys", classifiedKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        }
    }

    private static (OrderedDictionary<string, object?> Classification, List<OrderedDictionary<string, object?>> Remediations, List<string> ClassifiedKeys)
        BuildSecretMetadata(List<OrderedDictionary<string, object?>> sensitiveHints)
    {
        var entries = new List<OrderedDictionary<string, object?>>();
        var categoryCounter = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var hint in sensitiveHints)
        {
            var keyword = (string)hint["keyword"]!;
            var keyName = (string)hint["key"]!;
            var category = SecretCategoryMap.GetValueOrDefault(keyword, "credential");
            entries.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = keyName,
                ["keyword"] = keyword,
                ["category"] = category,
            });
            categoryCounter[category] = categoryCounter.GetValueOrDefault(category) + 1;
        }

        var categoryCounts = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (category, count) in categoryCounter)
        {
            categoryCounts[category] = count;
        }

        var classification = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entries"] = entries,
            ["category_counts"] = categoryCounts,
        };

        var remediations = new List<OrderedDictionary<string, object?>>();
        foreach (var (category, count) in categoryCounter)
        {
            var relatedKeys = entries
                .Where(entry => string.Equals((string)entry["category"]!, category, StringComparison.Ordinal) && ((string)entry["key"]!).Length > 0)
                .Select(entry => (string)entry["key"]!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var summary = string.Format(
                CultureInfo.InvariantCulture,
                "Rotate or scrub {0} values referenced in configuration",
                category.Replace('-', ' '));
            if (relatedKeys.Count > 0)
            {
                summary += $" ({string.Join(", ", relatedKeys)})";
            }

            remediations.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = $"ini-{category}-remediation",
                ["category"] = category,
                ["summary"] = summary,
                ["related_keys"] = relatedKeys,
                ["hint_count"] = count,
            });
        }

        var classifiedKeys = entries.Select(entry => (string)entry["key"]!).Where(key => key.Length > 0).ToList();
        return (classification, remediations, classifiedKeys);
    }
}
