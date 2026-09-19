namespace DriftBuster.Backend.Models;

/// <summary>The written package and the names of the config and runner script inside it.</summary>
public sealed record OfflineCollectorResult(string PackagePath, string ConfigFileName, string ScriptFileName);
