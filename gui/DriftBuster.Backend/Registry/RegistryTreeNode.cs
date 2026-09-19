namespace DriftBuster.Backend.Registry;

/// <summary>One key read from a registry: its subkey names and its values.</summary>
public sealed record RegistryTreeNode(string Hive, string Path, string? View, IReadOnlyList<string> Subkeys, IReadOnlyList<RegistryRawValue> Values);
