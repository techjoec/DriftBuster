namespace DriftBuster.Backend.Models;

/// <summary>The runs due at the reference time, in scheduled order; each is pending until completed.</summary>
public sealed record ScheduleDueResult(IReadOnlyList<ScheduleDueRun> Runs);
