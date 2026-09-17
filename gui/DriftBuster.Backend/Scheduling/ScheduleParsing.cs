using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The scheduler's parsing helpers for intervals, window times, time zones and timestamps. Payload values are in the
/// <see cref="EngineJson"/> domain; invalid input raises <see cref="ScheduleException"/>.
/// </summary>
public static partial class ScheduleParsing
{
    private static readonly string[] TimeFormats = ["H':'mm", "H':'mm':'ss"];

    // One compact interval token at the start of the text; ASCII digits only, because the number is parsed with the invariant culture.
    [GeneratedRegex(
        @"\A(?<value>[0-9]+(?:\.[0-9]+)?)(?<unit>[smhd])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IntervalToken();

    // The whole ISO-8601 time part after "PT".
    [GeneratedRegex(
        @"\A(?:(?<h>[0-9]+(?:\.[0-9]+)?)h)?(?:(?<m>[0-9]+(?:\.[0-9]+)?)m)?(?:(?<s>[0-9]+(?:\.[0-9]+)?)s)?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IsoDuration();

    /// <summary>
    /// A schedule interval: a <see cref="TimeSpan"/> as itself, an int, bool or float as seconds, anything else as text, stripped and
    /// lower-cased: <c>PT#H#M#S</c> ISO-8601 time durations, or compact tokens such as <c>15m</c>, <c>1h30m</c> and <c>1.5d</c>. Every
    /// result must be positive. An interval beyond the <see cref="TimeSpan"/> range raises <see cref="OverflowException"/>.
    /// </summary>
    public static TimeSpan ParseInterval(object? value)
    {
        switch (value)
        {
            case TimeSpan span:
                return Positive(span);
            case bool or int or long or BigInteger or double:
                var seconds = EngineBuiltins.Float(value);
                if (!(seconds > 0))
                {
                    throw new ScheduleException("Interval must be positive.");
                }

                return TimeSpan.FromSeconds(seconds);
        }

        var text = EngineText.Lower(EngineText.Strip(EngineRepr.Str(value)));
        if (text.Length == 0)
        {
            throw new ScheduleException("Interval text must not be empty.");
        }

        return text.StartsWith("pt", StringComparison.Ordinal) ? ParseIsoInterval(value, text) : ParseCompactInterval(text);
    }

    private static TimeSpan ParseIsoInterval(object? value, string text)
    {
        var match = IsoDuration().Match(text[2..]);
        if (!match.Success || match.Length == 0)
        {
            throw new ScheduleException("Unsupported ISO-8601 interval: " + EngineRepr.Repr(value));
        }

        var duration = TimeSpan.FromHours(GroupFloat(match, "h"))
            + TimeSpan.FromMinutes(GroupFloat(match, "m"))
            + TimeSpan.FromSeconds(GroupFloat(match, "s"));
        return Positive(duration);
    }

    private static double GroupFloat(Match match, string name)
        => match.Groups[name] is { Success: true, Length: > 0 } digits ? EngineBuiltins.Float(digits.Value) : 0.0;

    private static TimeSpan ParseCompactInterval(string text)
    {
        var cursor = 0;
        var total = TimeSpan.Zero;
        while (cursor < text.Length)
        {
            var match = IntervalToken().Match(text[cursor..]);
            if (!match.Success)
            {
                throw new ScheduleException("Unsupported interval fragment near: " + text[cursor..]);
            }

            var amount = EngineBuiltins.Float(match.Groups["value"].Value);
            total += match.Groups["unit"].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
            cursor += match.Index + match.Length;
        }

        return Positive(total);
    }

    private static TimeSpan Positive(TimeSpan interval)
        => interval > TimeSpan.Zero ? interval : throw new ScheduleException("Interval must be positive.");

    /// <summary>
    /// A window bound: <c>H:mm</c> or <c>H:mm:ss</c> (invariant culture, hours 0-23). Anything else, including an out-of-range field,
    /// raises <see cref="ScheduleException"/>.
    /// </summary>
    public static TimeOnly ParseTime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ScheduleException("Time must be HH:MM or HH:MM:SS");
    }

    /// <summary>
    /// A window time zone: <see cref="TimeZoneInfo.Utc"/> for an empty name, otherwise <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
    /// (IANA ids work on every platform). A name the system cannot resolve is "Unknown time zone".
    /// </summary>
    public static TimeZoneInfo BuildTimezone(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception exc) when (exc is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ScheduleException("Unknown time zone: " + name, exc);
        }
    }

    /// <summary>
    /// An ISO 8601 timestamp through <see cref="IsoTimestamp.TryParse"/> (text without an offset is UTC; the result is UTC). Failure raises
    /// <see cref="ScheduleException"/> with "Invalid ISO 8601 timestamp: 'text'".
    /// </summary>
    public static DateTimeOffset ParseIsoTimestamp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsoTimestamp.TryParse(text, out var instant)
            ? instant
            : throw new ScheduleException("Invalid ISO 8601 timestamp: " + EngineRepr.StrRepr(text));
    }

    /// <summary>
    /// A stored timestamp: the value as text through <see cref="IsoTimestamp.TryParse"/>. Empty text and text that does not parse raise
    /// <see cref="ScheduleException"/>.
    /// </summary>
    public static DateTimeOffset ParseTimestamp(object? value)
    {
        var text = EngineRepr.Str(value);
        if (text.Length == 0)
        {
            throw new ScheduleException("Timestamp payload must not be empty.");
        }

        return IsoTimestamp.TryParse(text, out var instant)
            ? instant
            : throw new ScheduleException("Unable to parse timestamp: " + EngineRepr.Repr(value));
    }
}
