using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The parsing helpers of <c>driftbuster.scheduler</c>: <c>parse_interval</c>, <c>_parse_time</c>, <c>_build_timezone</c>,
/// <c>_ensure_aware</c> and <c>_parse_timestamp</c>. Values are in the <see cref="PythonJson"/> domain; <c>ValueError</c> is
/// <see cref="PythonValueException"/>, <c>OverflowError</c> <see cref="OverflowException"/> and <c>ScheduleError</c>
/// <see cref="ScheduleException"/>.
/// </summary>
public static class ScheduleParsing
{
    private static readonly PythonPattern IntervalToken = PythonPattern.Compile(@"(?P<value>\d+(?:\.\d+)?)(?P<unit>[smhd])");

    // re.fullmatch of the ISO-8601 time part, spelled as a match anchored at the end.
    private static readonly PythonPattern IsoDuration =
        PythonPattern.Compile(@"(?:(?:(?P<h>\d+(?:\.\d+)?)h)?(?:(?P<m>\d+(?:\.\d+)?)m)?(?:(?P<s>\d+(?:\.\d+)?)s)?)\Z");

    /// <summary>
    /// <c>parse_interval(value)</c>: a <see cref="PythonTimeDelta"/> as itself, an int, bool or float as seconds, anything else
    /// <c>str()</c>-ed, stripped and lower-cased: <c>PT#H#M#S</c> ISO-8601 time durations, or compact tokens such as <c>15m</c>,
    /// <c>1h30m</c> and <c>1.5d</c>. Every result must be positive.
    /// </summary>
    public static PythonTimeDelta ParseInterval(object? value)
    {
        switch (value)
        {
            case PythonTimeDelta delta:
                return delta.IsPositive ? delta : throw new ScheduleException("Interval must be positive.");
            case bool or int or long or BigInteger or double:
                var seconds = PythonBuiltins.Float(value);
                if (seconds <= 0)
                {
                    throw new ScheduleException("Interval must be positive.");
                }

                return PythonTimeDelta.FromFloats(seconds: seconds);
        }

        var text = PythonText.Lower(PythonText.Strip(PythonRepr.Str(value)));
        if (text.Length == 0)
        {
            throw new ScheduleException("Interval text must not be empty.");
        }

        return text.StartsWith("pt", StringComparison.Ordinal) ? ParseIsoInterval(value, text) : ParseCompactInterval(text);
    }

    private static PythonTimeDelta ParseIsoInterval(object? value, string text)
    {
        var match = IsoDuration.Match(text[2..]);
        if (match is null || match.Value.Length == 0)
        {
            throw new ScheduleException("Unsupported ISO-8601 interval: " + PythonRepr.Repr(value));
        }

        var duration = PythonTimeDelta.FromFloats(
            hours: GroupFloat(match, "h"),
            minutes: GroupFloat(match, "m"),
            seconds: GroupFloat(match, "s"));
        return duration.IsPositive ? duration : throw new ScheduleException("Interval must be positive.");
    }

    private static double GroupFloat(PythonMatch match, string name)
        => match.Group(IsoDuration.GroupIndex[name]) is { Length: > 0 } digits ? PythonBuiltins.Float(digits) : 0.0;

    private static PythonTimeDelta ParseCompactInterval(string text)
    {
        var cursor = 0;
        var total = default(PythonTimeDelta);
        while (cursor < text.Length)
        {
            var match = IntervalToken.Match(text[cursor..])
                ?? throw new ScheduleException("Unsupported interval fragment near: " + text[cursor..]);
            var amount = PythonBuiltins.Float(match.Group(IntervalToken.GroupIndex["value"]));
            total += match.Group(IntervalToken.GroupIndex["unit"]) switch
            {
                "s" => PythonTimeDelta.FromFloats(seconds: amount),
                "m" => PythonTimeDelta.FromFloats(minutes: amount),
                "h" => PythonTimeDelta.FromFloats(hours: amount),
                _ => PythonTimeDelta.FromFloats(days: amount),
            };
            cursor += match.End;
        }

        return total.IsPositive ? total : throw new ScheduleException("Interval must be positive.");
    }

    /// <summary>
    /// <c>_parse_time(text)</c>: <c>HH:MM</c> or <c>HH:MM:SS</c> split on ":", each part through <c>int()</c> and the result through
    /// <c>datetime.time</c>.
    /// </summary>
    public static PythonTime ParseTime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            throw new ScheduleException("Time must be HH:MM or HH:MM:SS");
        }

        var hour = PythonBuiltins.Int(parts[0]);
        var minute = PythonBuiltins.Int(parts[1]);
        var second = parts.Length == 3 ? PythonBuiltins.Int(parts[2]) : BigInteger.Zero;
        return PythonTime.Create(hour, minute, second);
    }

    /// <summary><c>_build_timezone(name)</c>: UTC for an empty name, otherwise <c>ZoneInfo(name)</c>; any failure is "Unknown time zone".</summary>
    public static PythonTzInfo BuildTimezone(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return PythonFixedOffset.Utc;
        }

        try
        {
            return PythonZoneInfo.Create(name);
        }
        catch (Exception exc) when (exc is PythonValueException or TimeZoneNotFoundException)
        {
            throw new ScheduleException("Unknown time zone: " + name, exc);
        }
    }

    /// <summary><c>_ensure_aware(moment)</c>: a naive datetime is taken as UTC, an aware one is converted to UTC.</summary>
    public static PythonDateTime EnsureAware(PythonDateTime moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        return moment.Tz is null ? moment.WithTz(PythonFixedOffset.Utc) : moment.AsTimeZone(PythonFixedOffset.Utc);
    }

    /// <summary>
    /// <c>_parse_timestamp(value)</c>: <c>datetime.fromisoformat(str(value))</c> through <see cref="EnsureAware"/>; an empty text or a
    /// <c>ValueError</c> from the parser is a <see cref="ScheduleException"/>.
    /// </summary>
    public static PythonDateTime ParseTimestamp(object? value)
    {
        var text = PythonRepr.Str(value);
        if (text.Length == 0)
        {
            throw new ScheduleException("Timestamp payload must not be empty.");
        }

        PythonDateTime parsed;
        try
        {
            parsed = PythonDateTime.FromIsoFormat(text);
        }
        catch (PythonValueException exc)
        {
            throw new ScheduleException("Unable to parse timestamp: " + PythonRepr.Repr(value), exc);
        }

        return EnsureAware(parsed);
    }
}
