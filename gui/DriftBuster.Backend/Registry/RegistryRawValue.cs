namespace DriftBuster.Backend.Registry;

/// <summary>A registry value as stored: its name (empty for the key's default value), registry type and data bytes.</summary>
public sealed record RegistryRawValue(string Name, int Type, byte[] Data);
