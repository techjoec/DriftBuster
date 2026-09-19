using System.Text.Json;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>One hunt hit as read, the relative path it was matched by, and its matching configs.</summary>
public sealed record HuntBridgeItem(JsonElement Hunt, string? RelativePath, IReadOnlyList<HuntBridgeMatch> Profiles);
