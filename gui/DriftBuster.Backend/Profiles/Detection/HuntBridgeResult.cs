namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Hunt hits with the profile configs each one matches (<c>detection-profile hunt-bridge</c>).</summary>
public sealed record HuntBridgeResult(IReadOnlyList<HuntBridgeItem> Items);
