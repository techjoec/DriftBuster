using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary><c>scheduler._ScheduleState</c>: the next run of a schedule and the run handed out by <c>due</c> but not yet completed.</summary>
internal sealed class ScheduleState(EngineDateTime nextRun)
{
    public EngineDateTime NextRun { get; set; } = nextRun;

    public EngineDateTime? Pending { get; set; }
}
