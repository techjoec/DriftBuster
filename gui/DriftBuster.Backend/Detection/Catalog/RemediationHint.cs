namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>Recommended follow-up action for a detection class.</summary>
public sealed record RemediationHint(string Id, string Category, string Summary, string? Documentation = null);
