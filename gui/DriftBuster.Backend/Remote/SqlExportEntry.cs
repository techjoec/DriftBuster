namespace DriftBuster.Backend.Remote;

public sealed record SqlExportEntry(string Source, string Output, string Dialect, IReadOnlyDictionary<string, long> RowCounts);
