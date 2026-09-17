using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// ISO 8601 text for the instants DriftBuster writes into state files, payloads and reports. Instants carry whole microseconds:
/// <see cref="UtcNow"/> and <see cref="TryParse"/> drop the seventh fractional digit so written text reads back to the same value.
/// </summary>
public static class IsoTimestamp
{
    private const string DateTimePattern = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";
    private const string FractionPattern = "'.'ffffff";
    private const string OffsetPattern = "zzz";
    private const string TimeOfDayPattern = "HH':'mm':'ss";

    private static readonly string[] ParseFormats = BuildParseFormats();

    /// <summary>The current UTC instant, truncated to whole microseconds.</summary>
    public static DateTimeOffset UtcNow() => TruncateToMicroseconds(DateTimeOffset.UtcNow);

    /// <summary>The instant with any sub-microsecond ticks removed (towards the earlier instant).</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    /// <summary>
    /// <c>yyyy-MM-ddTHH:mm:ss</c>, then <c>.ffffff</c> only when the microsecond part is not zero, then the offset as <c>±HH:MM</c>
    /// (UTC is <c>+00:00</c>). The value is written in its own offset.
    /// </summary>
    public static string Format(DateTimeOffset value)
    {
        var truncated = TruncateToMicroseconds(value);
        var pattern = truncated.Ticks % TimeSpan.TicksPerSecond == 0
            ? DateTimePattern + OffsetPattern
            : DateTimePattern + FractionPattern + OffsetPattern;
        return truncated.ToString(pattern, CultureInfo.InvariantCulture);
    }

    /// <summary>A wall-clock time as <c>HH:mm:ss</c>, with <c>.ffffff</c> when the microsecond part is not zero.</summary>
    public static string FormatTimeOfDay(TimeOnly value)
    {
        var pattern = value.Ticks % TimeSpan.TicksPerSecond == 0 ? TimeOfDayPattern : TimeOfDayPattern + FractionPattern;
        return value.ToString(pattern, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses the ISO 8601 extended form: <c>yyyy-MM-dd</c> alone (midnight), or followed by <c>T</c> or a space, <c>HH:mm</c>, optional
    /// <c>:ss</c> with an optional fraction of one to seven digits, and an optional <c>Z</c> or <c>±HH:mm</c>. Text without an offset is
    /// UTC. The result is in UTC and truncated to whole microseconds. A time alone, or a value outside the <see cref="DateTimeOffset"/>
    /// range, fails.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (text is not null
            && DateTimeOffset.TryParseExact(text, ParseFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            value = TruncateToMicroseconds(parsed.ToUniversalTime());
            return true;
        }

        value = default;
        return false;
    }

    private static string[] BuildParseFormats()
    {
        string[] separators = ["'T'", "' '"];
        string[] offsets = [string.Empty, "'Z'", "zzz"];
        var times = new List<string> { "HH':'mm", "HH':'mm':'ss" };
        for (var digits = 1; digits <= 7; digits++)
        {
            times.Add("HH':'mm':'ss'.'" + new string('f', digits));
        }

        return ["yyyy'-'MM'-'dd", .. from separator in separators from time in times from offset in offsets select "yyyy'-'MM'-'dd" + separator + time + offset];
    }
}
