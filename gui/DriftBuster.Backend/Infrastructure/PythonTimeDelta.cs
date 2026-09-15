using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13's <c>datetime.timedelta</c>: normalised days, seconds (0..86399) and microseconds (0..999999), built from float
/// arguments exactly as <c>delta_new</c> builds them (whole parts in integer arithmetic, fractional parts summed and rounded half to
/// even), with the constructor's <c>OverflowError</c> (<see cref="OverflowException"/>) and <c>ValueError</c>
/// (<see cref="PythonValueException"/>) texts.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PythonTimeDelta : IComparable<PythonTimeDelta>
{
    /// <summary><c>timedelta.max.days</c>.</summary>
    public const int MaxDays = 999999999;

    private const long MicrosecondsPerSecond = 1_000_000;
    private const long SecondsPerDay = 86_400;

    private PythonTimeDelta(int days, int seconds, int microseconds)
    {
        Days = days;
        Seconds = seconds;
        Microseconds = microseconds;
    }

    public int Days { get; }

    public int Seconds { get; }

    public int Microseconds { get; }

    /// <summary>The whole value in microseconds.</summary>
    public BigInteger TotalMicroseconds => ((BigInteger)Days * SecondsPerDay + Seconds) * MicrosecondsPerSecond + Microseconds;

    /// <summary><c>bool(delta)</c> is true and the value is above zero: <c>total_seconds() &gt; 0</c>.</summary>
    public bool IsPositive => TotalMicroseconds.Sign > 0;

    /// <summary><c>total_seconds()</c>: the microseconds divided by 10**6 with correct rounding, as Python's true division of ints.</summary>
    public double TotalSeconds()
        => double.Parse(TotalMicroseconds.ToString(CultureInfo.InvariantCulture) + "E-6", NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary><c>timedelta(microseconds=total)</c> for an integer: floor division into days, seconds and microseconds.</summary>
    /// <exception cref="OverflowException">The day count leaves the C int range or <see cref="MaxDays"/>.</exception>
    public static PythonTimeDelta FromMicroseconds(BigInteger total)
    {
        var seconds = FloorDivRem(total, MicrosecondsPerSecond, out var microseconds);
        var days = FloorDivRem(seconds, SecondsPerDay, out var remainder);
        if (days < int.MinValue || days > int.MaxValue)
        {
            throw new OverflowException("Python int too large to convert to C int");
        }

        var dayCount = (int)days;
        if (dayCount < -MaxDays || dayCount > MaxDays)
        {
            throw new OverflowException(string.Create(CultureInfo.InvariantCulture, $"days={dayCount}; must have magnitude <= {MaxDays}"));
        }

        return new PythonTimeDelta(dayCount, (int)remainder, (int)microseconds);
    }

    /// <summary>
    /// <c>timedelta(days=..., seconds=..., minutes=..., hours=...)</c> with float arguments; a null argument is one not passed. The
    /// arguments are accumulated in <c>delta_new</c>'s order: seconds, minutes, hours, days.
    /// </summary>
    /// <exception cref="PythonValueException">A NaN argument.</exception>
    /// <exception cref="OverflowException">An infinite argument, or a result out of range.</exception>
    public static PythonTimeDelta FromFloats(double? days = null, double? seconds = null, double? minutes = null, double? hours = null)
    {
        var total = BigInteger.Zero;
        var leftover = 0.0;
        total = Accumulate(total, seconds, MicrosecondsPerSecond, ref leftover);
        total = Accumulate(total, minutes, 60 * MicrosecondsPerSecond, ref leftover);
        total = Accumulate(total, hours, 3600 * MicrosecondsPerSecond, ref leftover);
        total = Accumulate(total, days, SecondsPerDay * MicrosecondsPerSecond, ref leftover);
        if (leftover != 0.0)
        {
            var whole = Math.Round(leftover, MidpointRounding.AwayFromZero);
            if (Math.Abs(whole - leftover) == 0.5)
            {
                var odd = total.IsEven ? 0 : 1;
                whole = (2.0 * Math.Round((leftover + odd) * 0.5, MidpointRounding.AwayFromZero)) - odd;
            }

            total += (long)whole;
        }

        return FromMicroseconds(total);
    }

    /// <summary><c>left + right</c>.</summary>
    public static PythonTimeDelta operator +(PythonTimeDelta left, PythonTimeDelta right) => FromMicroseconds(left.TotalMicroseconds + right.TotalMicroseconds);

    public static bool operator <(PythonTimeDelta left, PythonTimeDelta right) => left.CompareTo(right) < 0;

    public static bool operator >(PythonTimeDelta left, PythonTimeDelta right) => left.CompareTo(right) > 0;

    public static bool operator <=(PythonTimeDelta left, PythonTimeDelta right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PythonTimeDelta left, PythonTimeDelta right) => left.CompareTo(right) >= 0;

    public int CompareTo(PythonTimeDelta other) => TotalMicroseconds.CompareTo(other.TotalMicroseconds);

    /// <summary><c>repr(delta)</c>: <c>datetime.timedelta(days=1, seconds=2, microseconds=3)</c>, zero fields omitted.</summary>
    public string Repr()
    {
        var parts = new List<string>(3);
        if (Days != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"days={Days}"));
        }

        if (Seconds != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"seconds={Seconds}"));
        }

        if (Microseconds != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"microseconds={Microseconds}"));
        }

        return "datetime.timedelta(" + (parts.Count == 0 ? "0" : string.Join(", ", parts)) + ")";
    }

    // accum() for a float argument: the whole part times the factor exactly, then the whole part of the fraction times the factor,
    // with what is left of that added to the running leftover.
    private static BigInteger Accumulate(BigInteger sofar, double? argument, long factor, ref double leftover)
    {
        if (argument is not { } number)
        {
            return sofar;
        }

        var wholePart = Math.Truncate(number);
        var fraction = double.IsInfinity(number) ? 0.0 : number - wholePart;
        var sum = sofar + (FromDouble(wholePart) * factor);
        if (fraction == 0.0)
        {
            return sum;
        }

        var scaled = factor * fraction;
        var scaledWhole = Math.Truncate(scaled);
        leftover += scaled - scaledWhole;
        return sum + FromDouble(scaledWhole);
    }

    // PyLong_FromDouble.
    private static BigInteger FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            throw new PythonValueException("cannot convert float NaN to integer", nameof(value));
        }

        if (double.IsInfinity(value))
        {
            throw new OverflowException("cannot convert float infinity to integer");
        }

        return new BigInteger(value);
    }

    // divmod for a positive divisor: the quotient rounded towards negative infinity and a non-negative remainder.
    private static BigInteger FloorDivRem(BigInteger value, long divisor, out BigInteger remainder)
    {
        var quotient = BigInteger.DivRem(value, divisor, out remainder);
        if (remainder.Sign < 0)
        {
            remainder += divisor;
            quotient -= 1;
        }

        return quotient;
    }
}
