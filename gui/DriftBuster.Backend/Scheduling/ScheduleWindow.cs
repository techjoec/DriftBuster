using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// A daily window between two wall-clock times in a time zone. A start after the end is an overnight window (22:00 to 02:00). Bounds
/// are inclusive. Wall times skipped or repeated by a daylight saving change resolve as <see cref="ZonedWallClock"/> describes.
/// </summary>
public sealed class ScheduleWindow
{
    public ScheduleWindow(TimeOnly start, TimeOnly end, TimeZoneInfo? timezone = null, string? timezoneName = null)
    {
        Start = start;
        End = end;
        Timezone = timezone ?? TimeZoneInfo.Utc;
        TimezoneName = string.IsNullOrEmpty(timezoneName) ? Timezone.Id : timezoneName;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public TimeZoneInfo Timezone { get; }

    /// <summary>The zone name as configured (<c>UTC</c> when none was given).</summary>
    public string TimezoneName { get; }

    /// <summary>
    /// The window from a payload: <c>start</c> and <c>end</c> as text (both required) and <c>timezone</c> (default <c>UTC</c>), the time
    /// zone resolved before either time is parsed.
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
        var name = EngineRepr.Str(payload.TryGetValue("timezone", out var zone) ? zone : "UTC");
        var timezone = ScheduleParsing.BuildTimezone(name);
        return new ScheduleWindow(ScheduleParsing.ParseTime(startText), ScheduleParsing.ParseTime(endText), timezone, name);
    }

    /// <summary>Whether the instant's wall-clock time of day in the window's zone, at full precision, lies inside the window.</summary>
    public bool Contains(DateTimeOffset moment) => Covers(WallClock(moment).TimeOfDay);

    /// <summary>
    /// The first run time at or after the candidate that the window allows, in UTC. The candidate is truncated to whole seconds; inside
    /// the window it is returned as is. Otherwise it moves to the window start: the same local day when it is before a daytime window or
    /// in an overnight window's closed hours, the next local day when it is after a daytime window. When that start time is repeated
    /// by a daylight saving change, the earliest occurrence not before the candidate is used; when it is skipped, the offset in force
    /// before the change applies.
    /// </summary>
    public DateTimeOffset Align(DateTimeOffset candidate)
    {
        var truncated = candidate.ToUniversalTime();
        truncated = truncated.AddTicks(-(truncated.Ticks % TimeSpan.TicksPerSecond));
        var local = WallClock(truncated);
        if (Covers(local.TimeOfDay))
        {
            return truncated;
        }

        var day = Start <= End && TimeOnly.FromTimeSpan(local.TimeOfDay) > End ? local.Date.AddDays(1) : local.Date;
        return ZonedWallClock.ToInstant(day.Add(Start.ToTimeSpan()), Timezone, notBefore: truncated);
    }

    private DateTime WallClock(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, Timezone).DateTime;

    private bool Covers(TimeSpan timeOfDay)
    {
        var current = TimeOnly.FromTimeSpan(timeOfDay);
        return Start <= End ? Start <= current && current <= End : current >= Start || current <= End;
    }
}
