namespace DriftBuster.Backend.Registry;

/// <summary><c>registry.scan.RegistryHit</c>: a value that matched a search, with the first 120 code points of its text.</summary>
public sealed record RegistryHit(string Path, string Hive, string ValueName, string DataPreview, string Reason);
