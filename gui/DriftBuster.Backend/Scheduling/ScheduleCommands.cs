using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The library half of <c>run_profiles_cli schedule</c>: <c>list</c>, <c>due</c>, <c>mark-complete</c> and <c>skip-until</c>, each
/// building the scheduler from the manifest and the state file and returning the payload the command prints as JSON (<c>due</c>,
/// <c>mark-complete</c> and <c>skip-until</c> write the state file first). Argument parsing and printing belong to the console tool.
/// </summary>
public static class ScheduleCommands
{
    /// <summary>
    /// <c>_build_scheduler(args)</c>: specs from the manifest (<see cref="ScheduleStore.DefaultConfigPath"/>), registered in manifest order,
    /// then the state file (<see cref="ScheduleStore.DefaultStatePath"/>) applied. Returns the scheduler and the state path.
    /// </summary>
    public static (ProfileScheduler Scheduler, string StatePath) BuildScheduler(string? baseDir, string? configPath = null, string? statePath = null)
    {
        var config = ScheduleStore.DefaultConfigPath(baseDir, configPath);
        var entries = ScheduleStore.LoadSchedulePayload(config);
        var specs = ScheduleStore.BuildScheduleSpecs(entries, baseDir);
        var scheduler = new ProfileScheduler(specs);
        var state = ScheduleStore.DefaultStatePath(baseDir, statePath);
        scheduler.ApplyState(ScheduleStore.LoadScheduleState(state));
        return (scheduler, state);
    }

    /// <summary>
    /// <c>_parse_reference_timestamp(value)</c>: <c>fromisoformat</c> (its <c>ValueError</c> becomes <see cref="CommandExitException"/>),
    /// naive as UTC, aware converted to UTC.
    /// </summary>
    public static PythonDateTime ParseReferenceTimestamp(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        PythonDateTime candidate;
        try
        {
            candidate = PythonDateTime.FromIsoFormat(value);
        }
        catch (PythonValueException exc)
        {
            throw new CommandExitException("Unable to parse timestamp: " + PythonRepr.StrRepr(value), exc);
        }

        return ScheduleParsing.EnsureAware(candidate);
    }

    /// <summary><c>_schedule_list(args)</c>: every schedule by name with its interval in seconds, tags, metadata, start, next run, pending run and window.</summary>
    public static IReadOnlyList<object?> List(string? baseDir, string? configPath = null, string? statePath = null)
    {
        var (scheduler, _) = BuildScheduler(baseDir, configPath, statePath);
        var snapshot = scheduler.SnapshotState();
        var payload = new List<object?>();
        foreach (var spec in scheduler.Schedules())
        {
            var state = snapshot.GetValueOrDefault(spec.Name) as OrderedDictionary<string, object?>;
            var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = spec.Name,
                ["profile"] = spec.Profile,
                ["interval_seconds"] = spec.Interval.TotalSeconds(),
                ["tags"] = spec.Tags.Cast<object?>().ToList(),
                ["metadata"] = new OrderedDictionary<string, object?>(spec.Metadata, StringComparer.Ordinal),
                ["start_at"] = spec.StartAt?.IsoFormat(),
                ["next_run"] = state?.GetValueOrDefault("next_run"),
                ["pending"] = state?.GetValueOrDefault("pending"),
            };
            if (spec.Window is { } window)
            {
                entry["window"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["start"] = window.Start.IsoFormat(),
                    ["end"] = window.End.IsoFormat(),
                    ["timezone"] = window.Timezone.Name,
                };
            }

            payload.Add(entry);
        }

        return payload;
    }

    /// <summary>
    /// <c>_schedule_due(args)</c>: the runs due at <paramref name="at"/> (when not empty, through <see cref="ParseReferenceTimestamp"/>;
    /// otherwise now), with the state file written after the scheduler marks them pending.
    /// </summary>
    public static IReadOnlyList<object?> Due(string? at, string? baseDir, string? configPath = null, string? statePath = null)
    {
        var (scheduler, state) = BuildScheduler(baseDir, configPath, statePath);
        var reference = string.IsNullOrEmpty(at) ? null : ParseReferenceTimestamp(at);
        var payload = scheduler.Due(reference)
            .Select(run => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = run.Name,
                ["profile"] = run.Profile,
                ["scheduled_for"] = run.ScheduledFor.IsoFormat(),
                ["tags"] = run.Tags.Cast<object?>().ToList(),
                ["metadata"] = new OrderedDictionary<string, object?>(run.Metadata, StringComparer.Ordinal),
            })
            .ToList();
        ScheduleStore.WriteScheduleState(scheduler, state);
        return payload;
    }

    /// <summary>
    /// <c>_schedule_mark_complete(args)</c>: completes the pending run (at <paramref name="completedAt"/> when not empty) and returns the
    /// schedule's <c>name</c>, <c>next_run</c> and <c>pending</c>; a <see cref="ScheduleException"/> becomes <see cref="CommandExitException"/>.
    /// </summary>
    public static OrderedDictionary<string, object?> MarkComplete(string name, string? completedAt, string? baseDir, string? configPath = null, string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var (scheduler, state) = BuildScheduler(baseDir, configPath, statePath);
        var completed = string.IsNullOrEmpty(completedAt) ? null : ParseReferenceTimestamp(completedAt);
        try
        {
            scheduler.MarkComplete(name, completed);
        }
        catch (ScheduleException exc)
        {
            throw new CommandExitException(exc.Message, exc);
        }

        return StateResult(name, scheduler, state);
    }

    /// <summary><c>_schedule_skip(args)</c>: restarts the schedule at <paramref name="resumeAt"/> and returns its <c>name</c>, <c>next_run</c> and <c>pending</c>.</summary>
    public static OrderedDictionary<string, object?> SkipUntil(string name, string resumeAt, string? baseDir, string? configPath = null, string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(resumeAt);
        var (scheduler, state) = BuildScheduler(baseDir, configPath, statePath);
        var resume = ParseReferenceTimestamp(resumeAt);
        try
        {
            scheduler.SkipUntil(name, resume);
        }
        catch (ScheduleException exc)
        {
            throw new CommandExitException(exc.Message, exc);
        }

        return StateResult(name, scheduler, state);
    }

    private static OrderedDictionary<string, object?> StateResult(string name, ProfileScheduler scheduler, string statePath)
    {
        var snapshot = scheduler.SnapshotState().GetValueOrDefault(name) as OrderedDictionary<string, object?>;
        var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["next_run"] = snapshot?.GetValueOrDefault("next_run"),
            ["pending"] = snapshot?.GetValueOrDefault("pending"),
        };
        ScheduleStore.WriteScheduleState(scheduler, statePath);
        return result;
    }
}
