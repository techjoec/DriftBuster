using System.Globalization;
using System.Numerics;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13's <c>datetime.datetime</c>: year 1 to 9999 fields with microseconds, an optional <see cref="EngineTzInfo"/> and the
/// PEP 495 <c>fold</c>. Arithmetic, <c>replace</c>, <c>astimezone</c>, comparisons, <c>isoformat</c> and <c>fromisoformat</c> follow
/// <c>_datetimemodule.c</c>: <c>datetime + timedelta</c> keeps the tzinfo and resets the fold, <c>replace</c> keeps both, and a result
/// outside the range raises <c>OverflowError("date value out of range")</c>.
/// </summary>
public sealed partial class EngineDateTime
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    private readonly DateTime _fields;

    private EngineDateTime(DateTime fields, EngineTzInfo? tz, int fold)
    {
        _fields = DateTime.SpecifyKind(fields, DateTimeKind.Unspecified);
        Tz = tz;
        Fold = fold;
    }

    public int Year => _fields.Year;

    public int Month => _fields.Month;

    public int Day => _fields.Day;

    public int Hour => _fields.Hour;

    public int Minute => _fields.Minute;

    public int Second => _fields.Second;

    public int Microsecond => (int)(_fields.Ticks % TimeSpan.TicksPerSecond / TicksPerMicrosecond);

    public EngineTzInfo? Tz { get; }

    public int Fold { get; }

    /// <summary>The fields as microseconds since 0001-01-01T00:00:00, ignoring the tzinfo.</summary>
    internal long FieldMicroseconds => _fields.Ticks / TicksPerMicrosecond;

    /// <summary>The fields as whole seconds since 0001-01-01T00:00:00 (<c>get_local_timestamp</c> without the epoch shift).</summary>
    internal long FieldSeconds => _fields.Ticks / TimeSpan.TicksPerSecond;

    /// <summary><c>datetime(year, month, day, hour, minute, second, microsecond, tzinfo, fold=fold)</c> with Python's range checks.</summary>
    public static EngineDateTime Create(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0,
        int second = 0,
        int microsecond = 0,
        EngineTzInfo? tz = null,
        int fold = 0)
    {
        if (year is < 1 or > 9999)
        {
            throw new EngineValueException(string.Create(CultureInfo.InvariantCulture, $"year {year} is out of range"), nameof(year));
        }

        if (month is < 1 or > 12)
        {
            throw new EngineValueException("month must be in 1..12", nameof(month));
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            throw new EngineValueException("day is out of range for month", nameof(day));
        }

        EngineTime.CheckTimeArgs(hour, minute, second, microsecond);
        if (fold is not (0 or 1))
        {
            throw new EngineValueException("fold must be either 0 or 1", nameof(fold));
        }

        var fields = new DateTime(year, month, day, hour, minute, second).AddTicks(microsecond * TicksPerMicrosecond);
        return new EngineDateTime(fields, tz, fold);
    }

    /// <summary><c>datetime.now(UTC)</c>: the current time to the microsecond on <see cref="EngineFixedOffset.Utc"/>.</summary>
    public static EngineDateTime UtcNow()
    {
        var now = DateTime.UtcNow;
        return new EngineDateTime(new DateTime(now.Ticks - (now.Ticks % TicksPerMicrosecond)), EngineFixedOffset.Utc, 0);
    }

    /// <summary><c>dt.utcoffset()</c> in microseconds; null for a naive datetime.</summary>
    public long? UtcOffset() => Tz?.UtcOffset(this);

    /// <summary><c>dt.replace(tzinfo=tz)</c>.</summary>
    public EngineDateTime WithTz(EngineTzInfo? tz) => new(_fields, tz, Fold);

    /// <summary><c>dt.replace(fold=fold)</c>.</summary>
    public EngineDateTime WithFold(int fold) => new(_fields, Tz, fold);

    /// <summary><c>dt.replace(hour=..., minute=..., second=..., microsecond=...)</c>: unspecified fields, the tzinfo and the fold are kept.</summary>
    public EngineDateTime Replace(int? hour = null, int? minute = null, int? second = null, int? microsecond = null)
        => Create(Year, Month, Day, hour ?? Hour, minute ?? Minute, second ?? Second, microsecond ?? Microsecond, Tz, Fold);

    /// <summary><c>dt.timetz().replace(tzinfo=None)</c>.</summary>
    public EngineTime TimeOfDay() => EngineTime.FromFields(Hour, Minute, Second, Microsecond);

    /// <summary><c>dt + delta</c>.</summary>
    public EngineDateTime Add(EngineTimeDelta delta) => FromFieldMicroseconds(FieldMicroseconds + delta.TotalMicroseconds, Tz);

    /// <summary><c>dt + timedelta(microseconds=microseconds)</c>.</summary>
    internal EngineDateTime AddMicroseconds(long microseconds) => FromFieldMicroseconds((BigInteger)FieldMicroseconds + microseconds, Tz);

    /// <summary>
    /// <c>dt.astimezone(tz)</c> for an aware datetime: itself when <paramref name="tz"/> is its own tzinfo object, otherwise its fields
    /// less its UTC offset handed to <c>tz.fromutc</c>.
    /// </summary>
    public EngineDateTime AsTimeZone(EngineTzInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (Tz is null)
        {
            throw new NotSupportedException("astimezone() of a naive datetime uses the system local time zone, which is not modelled.");
        }

        if (ReferenceEquals(Tz, tz))
        {
            return this;
        }

        var utc = AddMicroseconds(-Tz.UtcOffset(this)).WithTz(tz);
        return tz.FromUtc(utc);
    }

    /// <summary><c>dt.isoformat()</c>: <c>YYYY-MM-DDTHH:MM:SS</c>, <c>.ffffff</c> when the microsecond is not zero, then the UTC offset.</summary>
    public string IsoFormat()
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}-{Day:D2}T{TimeOfDay().IsoFormat()}");
        return UtcOffset() is { } offset ? text + EngineFixedOffset.FormatOffset(offset) : text;
    }

    public override string ToString() => IsoFormat();

    /// <summary>
    /// Python's ordering: datetimes sharing a tzinfo object (or both naive) compare by fields; aware datetimes with equal offsets
    /// compare by fields; otherwise by the UTC instant.
    /// </summary>
    /// <exception cref="EngineTypeException">One side is naive and the other aware.</exception>
    public int CompareWith(EngineDateTime other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(Tz, other.Tz))
        {
            return FieldMicroseconds.CompareTo(other.FieldMicroseconds);
        }

        var offset = UtcOffset();
        var otherOffset = other.UtcOffset();
        if (offset is null || otherOffset is null)
        {
            throw new EngineTypeException("can't compare offset-naive and offset-aware datetimes", nameof(other));
        }

        return offset == otherOffset
            ? FieldMicroseconds.CompareTo(other.FieldMicroseconds)
            : (FieldMicroseconds - offset.Value).CompareTo(other.FieldMicroseconds - otherOffset.Value);
    }

    public static bool operator <(EngineDateTime left, EngineDateTime right) => Order(left, right) < 0;

    public static bool operator >(EngineDateTime left, EngineDateTime right) => Order(left, right) > 0;

    public static bool operator <=(EngineDateTime left, EngineDateTime right) => Order(left, right) <= 0;

    public static bool operator >=(EngineDateTime left, EngineDateTime right) => Order(left, right) >= 0;

    private static int Order(EngineDateTime left, EngineDateTime right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareWith(right);
    }

    private static EngineDateTime FromFieldMicroseconds(BigInteger microseconds, EngineTzInfo? tz)
    {
        var maximum = DateTime.MaxValue.Ticks / TicksPerMicrosecond;
        if (microseconds.Sign < 0 || microseconds > maximum)
        {
            throw new OverflowException("date value out of range");
        }

        return new EngineDateTime(new DateTime((long)microseconds * TicksPerMicrosecond), tz, 0);
    }
}
