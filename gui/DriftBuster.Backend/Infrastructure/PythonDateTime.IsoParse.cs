namespace DriftBuster.Backend.Infrastructure;

/// <summary>The date and time halves of <c>datetime.fromisoformat</c>, with the C parser's return codes.</summary>
public sealed partial class PythonDateTime
{
    private const int DaysIn400Years = 146097;
    private const int DaysIn100Years = 36524;
    private const int DaysIn4Years = 1461;

    private static readonly int[] DaysBeforeMonth = [0, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

    // parse_isoformat_date: 0 on success, negative on failure. The length is compared as the C size_t it is, so -1 is unbounded.
    private static int ParseIsoDate(byte[] bytes, int length, IsoFields fields)
    {
        var position = 0;
        if (!ParseDigits(bytes, ref position, ref fields.Year, 4))
        {
            return -1;
        }

        var usesSeparator = bytes[position] == '-';
        if (usesSeparator)
        {
            position++;
        }

        if (bytes[position] == 'W')
        {
            return ParseIsoWeekDate(bytes, position + 1, length, usesSeparator, fields);
        }

        if (!ParseDigits(bytes, ref position, ref fields.Month, 2))
        {
            return -1;
        }

        if (usesSeparator && bytes[position++] != '-')
        {
            return -2;
        }

        return ParseDigits(bytes, ref position, ref fields.Day, 2) ? 0 : -1;
    }

    private static int ParseIsoWeekDate(byte[] bytes, int position, int length, bool usesSeparator, IsoFields fields)
    {
        var week = 0;
        var weekday = 0;
        if (!ParseDigits(bytes, ref position, ref week, 2))
        {
            return -3;
        }

        if ((ulong)position < (ulong)(long)length)
        {
            if (usesSeparator && bytes[position++] != '-')
            {
                return -2;
            }

            if (!ParseDigits(bytes, ref position, ref weekday, 1))
            {
                return -4;
            }
        }
        else
        {
            weekday = 1;
        }

        var rv = IsoToYmd(fields.Year, week, weekday, fields);
        return rv == 0 ? 0 : -3 + rv;
    }

    // iso_to_ymd.
    private static int IsoToYmd(int isoYear, int isoWeek, int isoDay, IsoFields fields)
    {
        if (isoYear is < 1 or > 9999)
        {
            return -4;
        }

        if (isoWeek is <= 0 or >= 53)
        {
            var firstWeekday = (YmdToOrdinal(isoYear, 1, 1) + 6) % 7;
            if (isoWeek != 53 || !(firstWeekday == 3 || (firstWeekday == 2 && DateTime.IsLeapYear(isoYear))))
            {
                return -2;
            }
        }

        if (isoDay is <= 0 or >= 8)
        {
            return -3;
        }

        var firstDay = YmdToOrdinal(isoYear, 1, 1);
        var firstWeekdayOfYear = (firstDay + 6) % 7;
        var weekOneMonday = firstDay - firstWeekdayOfYear + (firstWeekdayOfYear > 3 ? 7 : 0);
        OrdinalToYmd(weekOneMonday + ((isoWeek - 1) * 7) + isoDay - 1, fields);
        return 0;
    }

    private static int YmdToOrdinal(int year, int month, int day)
    {
        var previous = year - 1;
        var daysBeforeYear = (previous * 365) + (previous / 4) - (previous / 100) + (previous / 400);
        return daysBeforeYear + DaysBeforeMonth[month] + (month > 2 && DateTime.IsLeapYear(year) ? 1 : 0) + day;
    }

    // ord_to_ymd, which also yields year 10000 (rejected afterwards by the constructor).
    private static void OrdinalToYmd(int ordinal, IsoFields fields)
    {
        ordinal--;
        var n400 = ordinal / DaysIn400Years;
        var n = ordinal % DaysIn400Years;
        var year = (n400 * 400) + 1;
        var n100 = n / DaysIn100Years;
        n %= DaysIn100Years;
        var n4 = n / DaysIn4Years;
        n %= DaysIn4Years;
        var n1 = n / 365;
        n %= 365;
        year += (n100 * 100) + (n4 * 4) + n1;
        if (n1 == 4 || n100 == 4)
        {
            fields.Year = year - 1;
            fields.Month = 12;
            fields.Day = 31;
            return;
        }

        var leap = n1 == 3 && (n4 != 24 || n100 == 3);
        var month = (n + 50) >> 5;
        var preceding = DaysBeforeMonth[month] + (month > 2 && leap ? 1 : 0);
        if (preceding > n)
        {
            month--;
            preceding -= month == 2 && leap ? 29 : DateTime.DaysInMonth(2001, month);
        }

        fields.Year = year;
        fields.Month = month;
        fields.Day = n - preceding + 1;
    }

    // parse_isoformat_time: 0 without an offset, 1 with one, negative on failure.
    private static int ParseIsoTime(byte[] bytes, int start, int length, IsoFields fields)
    {
        var end = start + length;
        var tzPosition = start;
        do
        {
            if (bytes[tzPosition] is (byte)'Z' or (byte)'+' or (byte)'-')
            {
                break;
            }
        }
        while (++tzPosition < end);

        var rv = ParseClock(bytes, start, tzPosition, out fields.Hour, out fields.Minute, out fields.Second, out fields.Microsecond);
        if (rv < 0)
        {
            return rv;
        }

        if (tzPosition == end)
        {
            return rv == 1 ? -5 : 0;
        }

        if (bytes[tzPosition] == 'Z')
        {
            fields.TzOffset = 0;
            fields.TzMicrosecond = 0;
            return bytes[tzPosition + 1] != 0 ? -5 : 1;
        }

        var sign = bytes[tzPosition] == '-' ? -1 : 1;
        rv = ParseClock(bytes, tzPosition + 1, end, out var tzHour, out var tzMinute, out var tzSecond, out var tzMicrosecond);
        fields.TzOffset = sign * ((tzHour * 3600) + (tzMinute * 60) + tzSecond);
        fields.TzMicrosecond = sign * tzMicrosecond;
        return rv != 0 ? -5 : 1;
    }

    // parse_hh_mm_ss_ff: [HH[:?MM[:?SS]]] with the separator style fixed by the first one, then "." or "," and the fraction (six
    // digits kept, the rest skipped). 1 when something follows, -3 for a missing digit, -4 for a wrong separator.
    private static int ParseClock(byte[] bytes, int start, int end, out int hour, out int minute, out int second, out int microsecond)
    {
        var values = new int[3];
        hour = minute = second = microsecond = 0;
        var position = start;
        var hasSeparator = true;
        for (var index = 0; index < 3; index++)
        {
            if (!ParseDigits(bytes, ref position, ref values[index], 2))
            {
                return -3;
            }

            (hour, minute, second) = (values[0], values[1], values[2]);
            var ch = bytes[position++];
            if (index == 0)
            {
                hasSeparator = ch == ':';
            }

            if (position >= end)
            {
                return ch != 0 ? 1 : 0;
            }

            if (hasSeparator && ch == ':')
            {
                continue;
            }

            if (ch is (byte)'.' or (byte)',')
            {
                break;
            }

            if (hasSeparator)
            {
                return -4;
            }

            position--;
        }

        var toParse = Math.Min(end - position, 6);
        if (!ParseDigits(bytes, ref position, ref microsecond, toParse))
        {
            return -3;
        }

        if (toParse < 6)
        {
            microsecond *= FractionCorrection[toParse - 1];
        }

        while (IsDigit(bytes[position]))
        {
            position++;
        }

        return bytes[position] != 0 ? 1 : 0;
    }
}
