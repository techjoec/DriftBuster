namespace DriftBuster.Backend.MultiServer;

/// <summary>One diff cache file.</summary>
public sealed record DiffCacheEntry(string Signature, string Canonical);
