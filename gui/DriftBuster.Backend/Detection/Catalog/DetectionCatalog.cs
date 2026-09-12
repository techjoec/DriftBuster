namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>Priority-ordered detection class definitions.</summary>
public sealed record DetectionCatalog(string Version, string Updated, IReadOnlyList<FormatClass> Classes, FallbackClass Fallback)
{
    /// <summary>The runtime detection catalog.</summary>
    public static DetectionCatalog Default { get; } = DetectionCatalogData.Build();
}
