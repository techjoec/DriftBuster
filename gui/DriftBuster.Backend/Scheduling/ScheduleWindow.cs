using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// <c>scheduler.ScheduleWindow</c>: a daily window between two wall-clock times in a time zone. A start after the end is an overnight
/// window (22:00 to 02:00). Bounds are inclusive and compared with the microsecond of the moment.
/// </summary>
public sealed class ScheduleWindow
{
    public ScheduleWindow(EngineTime start, EngineTime end, EngineTzInfo? timezone = null)
    {
        Start = start;
        End = end;
        Timezone = timezone ?? EngineFixedOffset.Utc;
    }

    public EngineTime Start { get; }

    public EngineTime End { get; }

    public EngineTzInfo Timezone { get; }

    /// <summary>
    /// <c>ScheduleWindow.from_dict(payload)</c>: <c>str()</c> of <c>start</c> and <c>end</c> (both required), the time zone built from
    /// <c>str(payload.get("timezone", "UTC"))</c> before either time is parsed.
    /// </summary>
    public static ScheduleWindow FromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.TryGetValue("start", out var start) || !payload.TryGetValue("end", out var end))
        {
            throw new ScheduleException("Window requires start and end fields");
        }

        var startText = EngineRepr.Str(start);
        var endText = EngineRepr.Str(end);
        var timezone = ScheduleParsing.BuildTimezone(EngineRepr.Str(payload.TryGetValue("timezone", out var name) ? name : "UTC"));
        return new ScheduleWindow(ScheduleParsing.ParseTime(startText), ScheduleParsing.ParseTime(endText), timezone);
    }

    /// <summary><c>window.contains(moment)</c> for an aware moment: its wall-clock time in the window's zone lies inside the window.</summary>
    public bool Contains(EngineDateTime moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var current = moment.AsTimeZone(Timezone).TimeOfDay();
        return Start <= End ? Start <= current && current <= End : current >= Start || current <= End;
    }

    /// <summary>
    /// <c>window.align(candidate)</c>: the candidate in the window's zone without microseconds, moved to the window start the same day
    /// when it is before a daytime window or inside an overnight window's closed hours, or to the start the next day when it is after
    /// a daytime window, then converted to UTC. Moving within the day keeps the fold; moving to the next day resets it.
    /// </summary>
    public EngineDateTime Align(EngineDateTime candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var baseline = candidate.AsTimeZone(Timezone).Replace(microsecond: 0);
        var current = baseline.TimeOfDay();
        if (Start <= End)
        {
            if (current < Start)
            {
                baseline = AtStart(baseline);
            }
            else if (current > End)
            {
                baseline = AtStart(baseline.Add(EngineTimeDelta.FromMicroseconds(86_400_000_000)));
            }
        }
        else if (current > End && current < Start)
        {
            baseline = AtStart(baseline);
        }

        return baseline.AsTimeZone(EngineFixedOffset.Utc);
    }

    private EngineDateTime AtStart(EngineDateTime moment) => moment.Replace(Start.Hour, Start.Minute, Start.Second);
}
