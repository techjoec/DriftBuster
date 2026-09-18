namespace DriftBuster.Backend.History;

/// <summary>How much history is kept: runs, distinct file versions, and the database size in bytes.</summary>
public sealed record HistoryStats(int Runs, int FileVersions, long Bytes);
