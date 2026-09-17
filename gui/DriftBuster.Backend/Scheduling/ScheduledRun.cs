using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Scheduling;

/// <summary><c>scheduler.ScheduledRun</c>: a due run of a schedule, with the schedule's tags and metadata.</summary>
public sealed record ScheduledRun(
    string Name,
    string Profile,
    DateTimeOffset ScheduledFor,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, object?> Metadata)
{
    /// <summary><c>run.load_profile(loader)</c>.</summary>
    public RunProfile LoadProfile(Func<string, RunProfile>? loader = null)
        => loader is null ? throw new ScheduleException("A profile loader is required to hydrate the run.") : loader(Profile);
}
