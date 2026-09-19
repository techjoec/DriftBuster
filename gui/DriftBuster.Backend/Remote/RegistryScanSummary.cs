namespace DriftBuster.Backend.Remote;

/// <summary>A registry scan output file referenced by a capture: its token, the roots it covered and how many hits it holds.</summary>
public sealed record RegistryScanSummary(string File, string Path, string? Token, IReadOnlyList<string> Roots, IReadOnlyList<string> RequestedRoots, int HitCount);
