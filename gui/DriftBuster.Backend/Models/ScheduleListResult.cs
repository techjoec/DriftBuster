namespace DriftBuster.Backend.Models;

/// <summary>The manifest's schedules in manifest order.</summary>
public sealed record ScheduleListResult(IReadOnlyList<ScheduleDefinition> Schedules);
