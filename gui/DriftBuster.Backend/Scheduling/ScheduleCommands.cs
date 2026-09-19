using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The <c>schedule</c> operations (<c>list</c>, <c>due</c>, <c>mark-complete</c>, <c>skip-until</c>) over the manifest and the state
/// file; <c>due</c>, <c>mark-complete</c> and <c>skip-until</c> save the state. Failures raise <see cref="ScheduleException"/>.
/// </summary>
public sealed class ScheduleCommands(string? baseDir, string? configPath = null, string? statePath = null, TimeProvider? time = null)
{
    private readonly string _configPath = ScheduleStore.DefaultConfigPath(baseDir, configPath);
    private readonly string _statePath = ScheduleStore.DefaultStatePath(baseDir, statePath);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Every schedule by name with its interval, tags, metadata, start, window and state.</summary>
    public ScheduleStatusListResult List()
    {
        var scheduler = Load();
        return new ScheduleStatusListResult([.. scheduler.Schedules().Select(spec =>
        {
            var state = scheduler.StateOf(spec.Name);
            return new ScheduleStatus(
                spec.Name,
                spec.Profile,
                spec.Interval.TotalSeconds,
                spec.Tags,
                spec.Metadata,
                spec.StartAt,
                state.NextRun,
                state.Pending,
                spec.Window is { } window ? window.ToDefinition() : null);
        })]);
    }

    /// <summary>The runs due at <paramref name="at"/> (now when null), each marked pending.</summary>
    public ScheduleDueResult Due(DateTimeOffset? at = null)
    {
        var scheduler = Load();
        var runs = scheduler.Due(at ?? _time.GetUtcNow());
        ScheduleStore.SaveState(scheduler, _statePath);
        return new ScheduleDueResult([.. runs.Select(run => new ScheduleDueRun(run.Name, run.Profile, run.ScheduledFor, run.Tags, run.Metadata))]);
    }

    /// <summary>Completes the pending run (at <paramref name="completedAt"/>, else its scheduled time) and advances the schedule.</summary>
    public ScheduleStateResult MarkComplete(string name, DateTimeOffset? completedAt = null)
    {
        var scheduler = Load();
        scheduler.MarkComplete(name, completedAt);
        return Save(scheduler, name);
    }

    /// <summary>Clears any pending run and restarts the schedule at <paramref name="resumeAt"/>, aligned.</summary>
    public ScheduleStateResult SkipUntil(string name, DateTimeOffset resumeAt)
    {
        var scheduler = Load();
        scheduler.SkipUntil(name, resumeAt);
        return Save(scheduler, name);
    }

    private ProfileScheduler Load()
    {
        var specs = ScheduleStore.Validate(ScheduleStore.LoadManifest(_configPath).Schedules);
        var scheduler = new ProfileScheduler(specs, _time);
        scheduler.ApplyState(ScheduleStore.LoadState(_statePath));
        return scheduler;
    }

    private ScheduleStateResult Save(ProfileScheduler scheduler, string name)
    {
        ScheduleStore.SaveState(scheduler, _statePath);
        var state = scheduler.StateOf(name);
        return new ScheduleStateResult(name, state.NextRun, state.Pending);
    }
}
