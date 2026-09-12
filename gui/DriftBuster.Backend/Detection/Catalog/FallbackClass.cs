namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>The class assigned when no detection class matches.</summary>
public sealed record FallbackClass(string Name, string Slug, int Priority, string DefaultSeverity, IReadOnlyList<string>? Aliases = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
}
