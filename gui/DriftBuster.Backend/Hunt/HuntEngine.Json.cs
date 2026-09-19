using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Hunt;

public static partial class HuntEngine
{
    /// <summary>
    /// The hits as written out: rule (name, description, token name, keywords, pattern sources), path, <c>relative_path</c> (forward
    /// slashes, relative to the root directory, or the file name when that fails), line number, excerpt and, when the rule names a
    /// token, <c>metadata.plan_transform</c>.
    /// </summary>
    public static HuntHit[] ToHits(HuntScanResult result, string placeholderTemplate = DefaultPlaceholderTemplate)
    {
        ArgumentNullException.ThrowIfNull(result);
        return [.. result.Hits.Select(hit => ToHit(hit, result.RootDirectory, placeholderTemplate))];
    }

    private static HuntHit ToHit(HuntFinding hit, string rootDirectory, string placeholderTemplate)
    {
        var transform = PlanTransformForHit(hit, placeholderTemplate);
        return new HuntHit
        {
            Rule = new HuntRuleSummary
            {
                Name = hit.Rule.Name,
                Description = hit.Rule.Description,
                TokenName = hit.Rule.TokenName,
                Keywords = [.. hit.Rule.Keywords],
                Patterns = [.. hit.Rule.Patterns.Select(pattern => pattern.ToString())],
            },
            Path = hit.Path,
            RelativePath = RelativeTo(hit.Path, rootDirectory) ?? PathText.Name(hit.Path),
            LineNumber = hit.LineNumber,
            Excerpt = hit.Excerpt,
            Metadata = transform is null
                ? null
                : new HuntHitMetadata
                {
                    PlanTransform = new HuntPlanTransform
                    {
                        TokenName = transform.TokenName,
                        Value = transform.Value,
                        Placeholder = transform.Placeholder,
                        RuleName = transform.RuleName,
                    },
                },
        };
    }
}
