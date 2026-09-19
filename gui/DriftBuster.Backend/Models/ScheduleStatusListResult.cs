namespace DriftBuster.Backend.Models;

/// <summary>The schedules <c>schedule list</c> reports, ordered by name.</summary>
public sealed record ScheduleStatusListResult(IReadOnlyList<ScheduleStatus> Schedules);
