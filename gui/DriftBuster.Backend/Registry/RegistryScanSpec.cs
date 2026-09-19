using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Registry;

/// <summary>What a registry scan searches: the app token, keywords and .NET regular expressions, limits, explicit roots and remote hosts.</summary>
public sealed record RegistryScanSpec(
    string Token,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Patterns,
    int MaxDepth,
    int MaxHits,
    double TimeBudgetS,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RegistryRootEntrySpec>? Roots,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegistryRemoteTarget? Remote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RegistryRemoteTarget>? RemoteBatch);
