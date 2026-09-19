using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Registry;

public sealed record RegistryRootEntrySpec(string Hive, string Path, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? View);
