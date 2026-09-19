namespace DriftBuster.Backend.Models;

/// <summary>A schedule's state after <c>schedule mark-complete</c> or <c>schedule skip-until</c>.</summary>
public sealed record ScheduleStateResult(string Name, DateTimeOffset NextRun, DateTimeOffset? Pending);
