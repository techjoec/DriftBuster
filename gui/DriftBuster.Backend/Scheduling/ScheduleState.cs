namespace DriftBuster.Backend.Scheduling;

/// <summary><c>scheduler._ScheduleState</c>: the next run of a schedule and the run handed out by <c>due</c> but not yet completed.</summary>
internal sealed class ScheduleState(DateTimeOffset nextRun)
{
    public DateTimeOffset NextRun { get; set; } = nextRun;

    public DateTimeOffset? Pending { get; set; }
}
