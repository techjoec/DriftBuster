namespace DriftBuster.Backend.History;

/// <summary>One recorded setting value: which run, which server, which file, and the value (null when it was masked).</summary>
public sealed record HistoryEntry(
    long RunId,
    DateTimeOffset RecordedAt,
    string HostLabel,
    string Path,
    string Key,
    string? Value,
    bool Masked,
    string ValueHash)
{
    /// <summary>When the run was recorded, in this machine's time zone, for display.</summary>
    public DateTimeOffset RecordedAtLocal => RecordedAt.ToLocalTime();
}
