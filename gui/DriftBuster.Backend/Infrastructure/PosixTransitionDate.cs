namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// One date of a POSIX TZ string's DST rule as <c>_zoneinfo.c</c> holds it: <c>Mm.n.d</c> (<c>CalendarRule</c>), <c>Jn</c> or <c>n</c>
/// (<c>DayRule</c>), each with a transition time whose hour may range over -167..167.
/// </summary>
internal readonly record struct PosixTransitionDate(bool Calendar, bool Julian, int Month, int Week, int Day, int Hour, int Minute, int Second)
{
    private const long EpochOrdinal = 719163;

    private static readonly int[] DaysBeforeMonth = [-1, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

    private static readonly int[] DaysInMonth = [-1, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary><c>calendarrule_year_to_timestamp</c> / <c>dayrule_year_to_timestamp</c>: the local seconds since 1970 of the date in <paramref name="year"/>.</summary>
    public long YearToTimestamp(int year)
    {
        long ordinal;
        if (Calendar)
        {
            var firstDay = (int)((YmdToOrdinal(year, Month, 1) + 6) % 7);
            var daysInMonth = DaysInMonth[Month] + (Month == 2 && DateTime.IsLeapYear(year) ? 1 : 0);
            var monthDay = (Day - (firstDay + 1)) % 7;
            if (monthDay < 0)
            {
                monthDay += 7;
            }

            monthDay += 1 + ((Week - 1) * 7);
            if (monthDay > daysInMonth)
            {
                monthDay -= 7;
            }

            ordinal = YmdToOrdinal(year, Month, monthDay) - EpochOrdinal;
        }
        else
        {
            var day = Day;
            if (Julian && day >= 59 && DateTime.IsLeapYear(year))
            {
                day++;
            }

            ordinal = YmdToOrdinal(year, 1, 1) - EpochOrdinal - 1 + day;
        }

        return (ordinal * 86400) + ((long)Hour * 3600) + ((long)Minute * 60) + Second;
    }

    // ymd_to_ord: days since 0001-01-01 counted from 1.
    private static long YmdToOrdinal(int year, int month, int day)
    {
        long y = year - 1;
        var yearDay = DaysBeforeMonth[month] + (month > 2 && DateTime.IsLeapYear(year) ? 1 : 0);
        return (y * 365) + (y / 4) - (y / 100) + (y / 400) + yearDay + day;
    }
}
