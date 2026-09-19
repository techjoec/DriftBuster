using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DriftBuster.Backend.Scheduling;

/// <summary>Parsing for schedule fields; invalid input raises <see cref="ScheduleException"/>.</summary>
public static partial class ScheduleParsing
{
    private static readonly string[] TimeFormats = ["H':'mm", "H':'mm':'ss"];

    // ISO 8601 date-times: seconds and fractions optional, 'T' or a space between date and time, offset or Z optional (K).
    private static readonly string[] TimestampFormats =
    [
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK", "yyyy'-'MM'-'dd'T'HH':'mm':'ssK", "yyyy'-'MM'-'dd'T'HH':'mmK",
        "yyyy'-'MM'-'dd' 'HH':'mm':'ss.FFFFFFFK", "yyyy'-'MM'-'dd' 'HH':'mm':'ssK", "yyyy'-'MM'-'dd' 'HH':'mmK",
        "yyyy'-'MM'-'dd",
    ];

    // A run of compact tokens: a number and one of s, m, h, d.
    [GeneratedRegex(@"\A(?:[0-9]+(?:\.[0-9]+)?[smhd])+\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CompactInterval();

    [GeneratedRegex(@"(?<value>[0-9]+(?:\.[0-9]+)?)(?<unit>[smhd])", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IntervalToken();

    /// <summary>
    /// Compact tokens (<c>15m</c>, <c>1h30m</c>, <c>1.5d</c>, case-insensitive) or an ISO 8601 duration (<c>PT2H</c>, <c>P1D</c>);
    /// the result must be positive.
    /// </summary>
    public static TimeSpan ParseInterval(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim().ToLowerInvariant();
        TimeSpan interval;
        if (trimmed.StartsWith('p'))
        {
            try
            {
                interval = XmlConvert.ToTimeSpan(trimmed.ToUpperInvariant());
            }
            catch (FormatException exc)
            {
                throw new ScheduleException($"Unsupported interval: '{text}'.", exc);
            }
        }
        else if (CompactInterval().IsMatch(trimmed))
        {
            interval = TimeSpan.Zero;
            foreach (Match token in IntervalToken().Matches(trimmed))
            {
                var amount = double.Parse(token.Groups["value"].Value, CultureInfo.InvariantCulture);
                interval += token.Groups["unit"].Value switch
                {
                    "s" => TimeSpan.FromSeconds(amount),
                    "m" => TimeSpan.FromMinutes(amount),
                    "h" => TimeSpan.FromHours(amount),
                    _ => TimeSpan.FromDays(amount),
                };
            }
        }
        else
        {
            throw new ScheduleException($"Unsupported interval: '{text}'.");
        }

        return interval > TimeSpan.Zero ? interval : throw new ScheduleException("Interval must be positive.");
    }

    /// <summary>A window bound: <c>H:mm</c> or <c>H:mm:ss</c>, hours 0-23.</summary>
    public static TimeOnly ParseTime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ScheduleException($"Time must be HH:MM or HH:MM:SS, not '{text}'.");
    }

    /// <summary>UTC for an empty name, otherwise <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> (IANA ids work on every platform).</summary>
    public static TimeZoneInfo BuildTimezone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
        }
        catch (Exception exc) when (exc is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ScheduleException($"Unknown time zone: '{name}'.", exc);
        }
    }

    /// <summary>An ISO 8601 date or date-time; one without an offset is UTC. The result is UTC.</summary>
    public static DateTimeOffset ParseTimestamp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return DateTimeOffset.TryParseExact(text.Trim(), TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : throw new ScheduleException($"Invalid ISO 8601 timestamp: '{text}'.");
    }
}
