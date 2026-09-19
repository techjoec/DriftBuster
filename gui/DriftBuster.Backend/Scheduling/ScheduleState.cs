namespace DriftBuster.Backend.Scheduling;

/// <summary>The next run of a schedule and the run handed out by <c>Due</c> but not yet completed.</summary>
internal sealed class ScheduleState(DateTimeOffset nextRun)
{
    public DateTimeOffset NextRun { get; set; } = nextRun;

    public DateTimeOffset? Pending { get; set; }
}
