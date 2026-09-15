using System.Globalization;

namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>datetime.timezone</c> without a name: a fixed offset strictly between -24 and +24 hours; <see cref="Utc"/> is <c>timezone.utc</c>.</summary>
public sealed class PythonFixedOffset : PythonTzInfo
{
    private const long MicrosecondsPerSecond = 1_000_000;

    private PythonFixedOffset(PythonTimeDelta offset)
    {
        Offset = offset;
    }

    /// <summary><c>datetime.UTC</c>.</summary>
    public static PythonFixedOffset Utc { get; } = new(default);

    public PythonTimeDelta Offset { get; }

    /// <summary><c>str(timezone)</c>: "UTC" for a zero offset, otherwise <c>UTC±HH:MM[:SS[.ffffff]]</c>.</summary>
    public override string Name => Offset.TotalMicroseconds.IsZero ? "UTC" : "UTC" + FormatOffset((long)Offset.TotalMicroseconds);

    /// <summary><c>new_timezone(offset, NULL)</c>: <see cref="Utc"/> for a zero offset, <c>ValueError</c> outside the range.</summary>
    public static PythonFixedOffset Create(PythonTimeDelta offset)
    {
        if (offset.TotalMicroseconds.IsZero)
        {
            return Utc;
        }

        if ((offset.Days == -1 && offset.Seconds == 0 && offset.Microseconds < 1) || offset.Days < -1 || offset.Days >= 1)
        {
            throw new PythonValueException(
                "offset must be a timedelta strictly between -timedelta(hours=24) and timedelta(hours=24), not " + offset.Repr() + ".",
                nameof(offset));
        }

        return new PythonFixedOffset(offset);
    }

    public override long UtcOffset(PythonDateTime moment) => (long)Offset.TotalMicroseconds;

    public override PythonDateTime FromUtc(PythonDateTime moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        return moment.AddMicroseconds((long)Offset.TotalMicroseconds);
    }

    /// <summary><c>format_utcoffset</c> with ":" separators: <c>±HH:MM</c>, then <c>:SS</c> and <c>.ffffff</c> when not zero.</summary>
    internal static string FormatOffset(long offsetMicroseconds)
    {
        var sign = offsetMicroseconds < 0 ? '-' : '+';
        var magnitude = Math.Abs(offsetMicroseconds);
        var microseconds = magnitude % MicrosecondsPerSecond;
        var seconds = magnitude / MicrosecondsPerSecond;
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        seconds %= 60;
        if (microseconds != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:D2}:{minutes:D2}:{seconds:D2}.{microseconds:D6}");
        }

        return seconds != 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:D2}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:D2}:{minutes:D2}");
    }
}
