using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// <c>scheduler.ProfileScheduler</c>: registered schedules in registration order, each with its next run and the run <see cref="Due"/>
/// handed out, which keeps being returned until <see cref="MarkComplete"/> advances the schedule. Every state time is UTC.
/// </summary>
public sealed class ProfileScheduler
{
    private readonly OrderedDictionary<string, ScheduleSpec> _specs = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, ScheduleState> _state = new(StringComparer.Ordinal);

    /// <summary><c>ProfileScheduler(specs)</c>: registers each spec in order.</summary>
    public ProfileScheduler(IEnumerable<ScheduleSpec>? specs = null)
    {
        foreach (var spec in specs ?? [])
        {
            Register(spec);
        }
    }

    /// <summary>The clock (UTC, whole microseconds); tests swap it.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = IsoTimestamp.UtcNow;

    /// <summary><c>scheduler.register(spec)</c>: a new name starts at <c>spec.initial_run(_now())</c>.</summary>
    public void Register(ScheduleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (_specs.ContainsKey(spec.Name))
        {
            throw new ScheduleException("Schedule already registered: " + spec.Name);
        }

        var start = spec.InitialRun(Now());
        _specs[spec.Name] = spec;
        _state[spec.Name] = new ScheduleState(start);
    }

    /// <summary><c>scheduler.schedules()</c>: the specs ordered by name code point.</summary>
    public IReadOnlyList<ScheduleSpec> Schedules()
    {
        var names = _specs.Keys.ToList();
        names.Sort(PathText.CompareCodePoints);
        return names.Select(name => _specs[name]).ToList();
    }

    /// <summary>
    /// <c>scheduler.due(reference)</c>: every pending run at or before the reference, and every schedule whose next run is at or before it
    /// (which becomes pending), ordered by scheduled time (stable, registration order for ties).
    /// </summary>
    public IReadOnlyList<ScheduledRun> Due(DateTimeOffset? reference = null)
    {
        var now = (reference ?? Now()).ToUniversalTime();
        var runs = new List<ScheduledRun>();
        foreach (var (name, spec) in _specs)
        {
            var state = _state[name];
            if (state.Pending is { } pending)
            {
                if (pending <= now)
                {
                    runs.Add(new ScheduledRun(name, spec.Profile, pending, spec.Tags, spec.Metadata));
                }

                continue;
            }

            if (state.NextRun <= now)
            {
                state.Pending = state.NextRun;
                runs.Add(new ScheduledRun(name, spec.Profile, state.NextRun, spec.Tags, spec.Metadata));
            }
        }

        // Stable: runs due at the same time keep registration order.
        return runs.Order(Comparer<ScheduledRun>.Create(static (left, right) =>
            left.ScheduledFor < right.ScheduledFor ? -1 : (right.ScheduledFor < left.ScheduledFor ? 1 : 0))).ToArray();
    }

    /// <summary><c>scheduler.peek(name)</c>: the pending run, else the next run.</summary>
    public DateTimeOffset Peek(string name)
    {
        var state = State(name);
        return state.Pending ?? state.NextRun;
    }

    /// <summary><c>scheduler.mark_complete(name, completed_at)</c>: clears the pending run; the next run follows the completion time (the pending time by default).</summary>
    public void MarkComplete(string name, DateTimeOffset? completedAt = null)
    {
        var state = State(name);
        if (state.Pending is null)
        {
            throw new ScheduleException($"Schedule {name} is not pending.");
        }

        var completed = (completedAt ?? state.Pending.Value).ToUniversalTime();
        state.Pending = null;
        state.NextRun = _specs[name].NextAfter(completed);
    }

    /// <summary><c>scheduler.skip_until(name, resume_at)</c>: clears the pending run and restarts at the resume time, aligned.</summary>
    public void SkipUntil(string name, DateTimeOffset resumeAt)
    {
        var state = State(name);
        state.Pending = null;
        state.NextRun = _specs[name].AlignTo(resumeAt);
    }

    /// <summary><c>scheduler.cancel(name)</c>.</summary>
    public void Cancel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_specs.Remove(name))
        {
            throw new ScheduleException("Unknown schedule: " + name);
        }

        _state.Remove(name);
    }

    /// <summary><c>scheduler.snapshot_state()</c>: per schedule in registration order, <c>next_run</c> and <c>pending</c> as ISO strings (pending may be null).</summary>
    public OrderedDictionary<string, object?> SnapshotState()
    {
        var snapshot = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, state) in _state)
        {
            snapshot[name] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["next_run"] = IsoTimestamp.Format(state.NextRun),
                ["pending"] = state.Pending is { } pending ? IsoTimestamp.Format(pending) : null,
            };
        }

        return snapshot;
    }

    /// <summary>
    /// <c>scheduler.apply_state(state)</c>: for each registered name in the mapping, a truthy <c>next_run</c> replaces the next run and
    /// <c>pending</c> is set from a truthy value or cleared; both go through <see cref="ScheduleParsing.ParseTimestamp"/>.
    /// </summary>
    public void ApplyState(IReadOnlyDictionary<string, object?> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var (name, payload) in state)
        {
            if (!_state.TryGetValue(name, out var entry))
            {
                continue;
            }

            var nextRun = EngineBuiltins.Get(payload, "next_run");
            if (EngineBuiltins.IsTruthy(nextRun))
            {
                entry.NextRun = ScheduleParsing.ParseTimestamp(nextRun);
            }

            var pending = EngineBuiltins.Get(payload, "pending");
            entry.Pending = EngineBuiltins.IsTruthy(pending) ? ScheduleParsing.ParseTimestamp(pending) : null;
        }
    }

    private ScheduleState State(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _state.TryGetValue(name, out var state) ? state : throw new ScheduleException("Unknown schedule: " + name);
    }
}
