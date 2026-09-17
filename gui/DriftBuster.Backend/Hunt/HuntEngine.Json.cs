using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Hunt;

/// <summary>The <c>return_json=True</c> shape of <c>hunt_path</c>.</summary>
public static partial class HuntEngine
{
    /// <summary>
    /// <c>hunt_path(..., return_json=True)</c> entries: <c>rule</c> (name, description, token_name, keywords, patterns),
    /// <c>path</c>, <c>relative_path</c> (posix, relative to the root directory, or the file name when that fails),
    /// <c>line_number</c>, <c>excerpt</c> and, when the hit yields a plan transform, <c>metadata.plan_transform</c>
    /// (token_name, value, placeholder, rule_name).
    /// </summary>
    public static IReadOnlyList<OrderedDictionary<string, object?>> ToJson(HuntScanResult result, string placeholderTemplate = DefaultPlaceholderTemplate)
    {
        ArgumentNullException.ThrowIfNull(result);
        var entries = new List<OrderedDictionary<string, object?>>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["rule"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = hit.Rule.Name,
                    ["description"] = hit.Rule.Description,
                    ["token_name"] = hit.Rule.TokenName,
                    ["keywords"] = hit.Rule.Keywords.Cast<object?>().ToList(),
                    ["patterns"] = hit.Rule.Patterns.Select(pattern => (object?)pattern.ToString()).ToList(),
                },
                ["path"] = hit.Path,
                ["relative_path"] = RelativeTo(hit.Path, result.RootDirectory) ?? PathText.Name(hit.Path),
                ["line_number"] = hit.LineNumber,
                ["excerpt"] = hit.Excerpt,
            };
            if (PlanTransformForHit(hit, placeholderTemplate) is { } transform)
            {
                entry["metadata"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["plan_transform"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["token_name"] = transform.TokenName,
                        ["value"] = transform.Value,
                        ["placeholder"] = transform.Placeholder,
                        ["rule_name"] = transform.RuleName,
                    },
                };
            }

            entries.Add(entry);
        }

        return entries;
    }
}
