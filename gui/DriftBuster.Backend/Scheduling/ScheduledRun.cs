using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Scheduling;

/// <summary>A due run of a schedule, with the schedule's tags and metadata.</summary>
public sealed record ScheduledRun(
    string Name,
    string Profile,
    DateTimeOffset ScheduledFor,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, object?> Metadata)
{
    public RunProfile LoadProfile(Func<string, RunProfile>? loader = null)
        => loader is null ? throw new ScheduleException("A profile loader is required to hydrate the run.") : loader(Profile);
}
