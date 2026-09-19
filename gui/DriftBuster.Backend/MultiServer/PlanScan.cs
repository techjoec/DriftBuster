namespace DriftBuster.Backend.MultiServer;

/// <summary>A host scan: records by config id, whether every record came from the cache, and whether the sample budget ran out.</summary>
/// <param name="SkippedFiles">Files the detector could not read and skipped.</param>
public sealed record PlanScan(
    OrderedDictionary<string, ConfigRecord> Configs,
    bool UsedCache,
    bool BudgetReached,
    IReadOnlyList<string> SkippedFiles);
