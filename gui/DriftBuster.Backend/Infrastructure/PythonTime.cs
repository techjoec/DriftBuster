using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>A naive <c>datetime.time</c>: ordered by hour, minute, second and microsecond, as Python compares times.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PythonTime : IComparable<PythonTime>
{
    private PythonTime(int hour, int minute, int second, int microsecond)
    {
        Hour = hour;
        Minute = minute;
        Second = second;
        Microsecond = microsecond;
    }

    public int Hour { get; }

    public int Minute { get; }

    public int Second { get; }

    public int Microsecond { get; }

    /// <summary>
    /// <c>time(hour=..., minute=..., second=...)</c> with Python int arguments: each argument is converted to a C int in order
    /// (<c>OverflowError</c>), then the ranges are checked in order (<c>ValueError</c>).
    /// </summary>
    public static PythonTime Create(BigInteger hour, BigInteger minute, BigInteger second)
    {
        var hourValue = ToCInt(hour);
        var minuteValue = ToCInt(minute);
        var secondValue = ToCInt(second);
        CheckTimeArgs(hourValue, minuteValue, secondValue, 0);
        return new PythonTime(hourValue, minuteValue, secondValue, 0);
    }

    /// <summary>A time from fields that are already in range, as <c>datetime.timetz()</c> builds it.</summary>
    internal static PythonTime FromFields(int hour, int minute, int second, int microsecond)
    {
        CheckTimeArgs(hour, minute, second, microsecond);
        return new PythonTime(hour, minute, second, microsecond);
    }

    /// <summary><c>check_time_args</c>.</summary>
    internal static void CheckTimeArgs(int hour, int minute, int second, int microsecond)
    {
        if (hour is < 0 or > 23)
        {
            throw new PythonValueException("hour must be in 0..23", nameof(hour));
        }

        if (minute is < 0 or > 59)
        {
            throw new PythonValueException("minute must be in 0..59", nameof(minute));
        }

        if (second is < 0 or > 59)
        {
            throw new PythonValueException("second must be in 0..59", nameof(second));
        }

        if (microsecond is < 0 or > 999999)
        {
            throw new PythonValueException("microsecond must be in 0..999999", nameof(microsecond));
        }
    }

    public static bool operator <(PythonTime left, PythonTime right) => left.CompareTo(right) < 0;

    public static bool operator >(PythonTime left, PythonTime right) => left.CompareTo(right) > 0;

    public static bool operator <=(PythonTime left, PythonTime right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PythonTime left, PythonTime right) => left.CompareTo(right) >= 0;

    public int CompareTo(PythonTime other)
        => (Hour, Minute, Second, Microsecond).CompareTo((other.Hour, other.Minute, other.Second, other.Microsecond));

    /// <summary><c>time.isoformat()</c>: <c>HH:MM:SS</c>, with <c>.ffffff</c> when the microsecond is not zero.</summary>
    public string IsoFormat()
        => Microsecond == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Hour:D2}:{Minute:D2}:{Second:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{Hour:D2}:{Minute:D2}:{Second:D2}.{Microsecond:D6}");

    // The "i" argument format: a C long first, then a C int, or OverflowError.
    private static int ToCInt(BigInteger value)
    {
        if (value > long.MaxValue || value < long.MinValue)
        {
            throw new OverflowException("Python int too large to convert to C long");
        }

        if (value > int.MaxValue)
        {
            throw new OverflowException("signed integer is greater than maximum");
        }

        if (value < int.MinValue)
        {
            throw new OverflowException("signed integer is less than minimum");
        }

        return (int)value;
    }
}
