namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// Registered schedules in registration order, each with its next run and the run <see cref="Due"/> handed out, which keeps being
/// returned until <see cref="MarkComplete"/> advances the schedule. Every state time is UTC.
/// </summary>
public sealed class ProfileScheduler
{
    private readonly TimeProvider _time;
    private readonly OrderedDictionary<string, ScheduleSpec> _specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScheduleState> _state = new(StringComparer.Ordinal);

    public ProfileScheduler(IEnumerable<ScheduleSpec> specs, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(specs);
        _time = time ?? TimeProvider.System;
        foreach (var spec in specs)
        {
            Register(spec);
        }
    }

    /// <summary>A new name starts at <see cref="ScheduleSpec.InitialRun"/> of now.</summary>
    public void Register(ScheduleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_specs.TryAdd(spec.Name, spec))
        {
            throw new ScheduleException($"Schedule already registered: '{spec.Name}'.");
        }

        _state[spec.Name] = new ScheduleState(spec.InitialRun(_time.GetUtcNow()));
    }

    /// <summary>The specs ordered by name.</summary>
    public IReadOnlyList<ScheduleSpec> Schedules() => [.. _specs.Values.OrderBy(spec => spec.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Every pending run at or before the reference, and every schedule whose next run is at or before it (which becomes pending),
    /// ordered by scheduled time, registration order for ties.
    /// </summary>
    public IReadOnlyList<ScheduledRun> Due(DateTimeOffset? reference = null)
    {
        var now = (reference ?? _time.GetUtcNow()).ToUniversalTime();
        var runs = new List<ScheduledRun>();
        foreach (var (name, spec) in _specs)
        {
            var state = _state[name];
            if (state.Pending is null && state.NextRun <= now)
            {
                state.Pending = state.NextRun;
            }

            if (state.Pending is { } pending && pending <= now)
            {
                runs.Add(new ScheduledRun(name, spec.Profile, pending, spec.Tags, spec.Metadata));
            }
        }

        return [.. runs.OrderBy(run => run.ScheduledFor)];
    }

    /// <summary>Clears the pending run; the next run follows the completion time (the pending time by default).</summary>
    public void MarkComplete(string name, DateTimeOffset? completedAt = null)
    {
        var state = State(name);
        var pending = state.Pending ?? throw new ScheduleException($"Schedule '{name}' is not pending.");
        state.Pending = null;
        state.NextRun = _specs[name].NextAfter(completedAt ?? pending);
    }

    /// <summary>Clears the pending run and restarts at the resume time, aligned.</summary>
    public void SkipUntil(string name, DateTimeOffset resumeAt)
    {
        var state = State(name);
        state.Pending = null;
        state.NextRun = _specs[name].AlignTo(resumeAt);
    }

    /// <summary>The state of every schedule, for <c>scheduler-state.json</c>.</summary>
    public IReadOnlyDictionary<string, ScheduleStateEntry> SnapshotState()
        => _state.ToDictionary(pair => pair.Key, pair => new ScheduleStateEntry(pair.Value.NextRun, pair.Value.Pending), StringComparer.Ordinal);

    /// <summary>The saved state of each registered schedule; entries for names no longer in the manifest are ignored.</summary>
    public void ApplyState(IReadOnlyDictionary<string, ScheduleStateEntry> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        foreach (var (name, entry) in saved)
        {
            if (_state.TryGetValue(name, out var state))
            {
                state.NextRun = entry.NextRun.ToUniversalTime();
                state.Pending = entry.Pending?.ToUniversalTime();
            }
        }
    }

    /// <summary>The state of one schedule.</summary>
    public ScheduleStateEntry StateOf(string name)
    {
        var state = State(name);
        return new ScheduleStateEntry(state.NextRun, state.Pending);
    }

    private ScheduleState State(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _state.TryGetValue(name, out var state) ? state : throw new ScheduleException($"Unknown schedule: '{name}'.");
    }
}
