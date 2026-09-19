namespace DriftBuster.Backend.Scheduling;

/// <summary>One schedule's entry in <c>scheduler-state.json</c>: its next run and the run handed out but not yet completed.</summary>
public sealed record ScheduleStateEntry(DateTimeOffset NextRun, DateTimeOffset? Pending);
