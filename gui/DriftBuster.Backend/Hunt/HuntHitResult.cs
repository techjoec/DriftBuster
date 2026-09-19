namespace DriftBuster.Backend.Hunt;

/// <summary>A hunt hit as DriftBuster writes it: the rule, the file (and its path under the scanned root), the 1-based line and the excerpt.</summary>
public sealed record HuntHitResult(HuntRuleResult Rule, string Path, string? RelativePath, int LineNumber, string Excerpt)
{
    public static HuntHitResult From(HuntFinding hit, string? root)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return new HuntHitResult(
            new HuntRuleResult(hit.Rule.Name, hit.Rule.Description, hit.Rule.TokenName, hit.Rule.Keywords, [.. hit.Rule.Patterns.Select(pattern => pattern.ToString())]),
            hit.Path,
            root is null ? null : System.IO.Path.GetRelativePath(root, hit.Path).Replace('\\', '/'),
            hit.LineNumber,
            hit.Excerpt);
    }
}
