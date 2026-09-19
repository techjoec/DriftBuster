using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Registry;

/// <summary>A <c>registry_scan</c> source for an offline runner config, as <c>registry-scan emit-config</c> prints it.</summary>
public sealed record RegistryScanConfig(
    RegistryScanSpec RegistryScan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alias);
