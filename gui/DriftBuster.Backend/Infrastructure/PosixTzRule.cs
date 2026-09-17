namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>_zoneinfo.c</c>'s <c>_tzrule</c>: the offsets a zone follows after its last explicit transition, parsed from the TZif footer
/// (<c>parse_tz_str</c>) or built from its last local time type. A standard-only rule has one offset; otherwise a DST offset applies between
/// the rule's start and end dates of each year.
/// </summary>
internal sealed class PosixTzRule
{
    private readonly PosixTransitionDate _start;
    private readonly PosixTransitionDate _end;

    private PosixTzRule(long standardOffset, long? dstOffset, PosixTransitionDate start, PosixTransitionDate end)
    {
        StandardOffset = standardOffset;
        StandardOnly = dstOffset is null;
        DstOffset = dstOffset ?? standardOffset;
        DstDiff = DstOffset - standardOffset;
        _start = start;
        _end = end;
    }

    public long StandardOffset { get; }

    public long DstOffset { get; }

    public long DstDiff { get; }

    public bool StandardOnly { get; }

    /// <summary><c>build_tzrule</c> for a standard-only rule at <paramref name="offset"/> seconds.</summary>
    public static PosixTzRule Fixed(long offset) => new(offset, dstOffset: null, default, default);

    /// <summary>
    /// <c>parse_tz_str</c>: <c>std offset[dst[offset],start[/time],end[/time]]</c>, each abbreviation alphabetic or <c>&lt;...&gt;</c>
    /// quoted, offsets within 24 hours, a missing DST offset one hour ahead of standard time; <c>ValueError</c> otherwise.
    /// </summary>
    public static PosixTzRule Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var position = 0;
        if (!ParseAbbreviation(text, ref position))
        {
            throw Invalid($"Invalid STD format in {Repr(text)}");
        }

        if (!ParseOffset(text, ref position, out var standard))
        {
            throw Invalid($"Invalid STD offset in {Repr(text)}");
        }

        if (position == text.Length)
        {
            return Fixed(standard);
        }

        if (!ParseAbbreviation(text, ref position))
        {
            throw Invalid($"Invalid DST format in {Repr(text)}");
        }

        long dst;
        if (At(text, position) == ',')
        {
            dst = standard + 3600;
        }
        else if (!ParseOffset(text, ref position, out dst))
        {
            throw Invalid($"Invalid DST offset in {Repr(text)}");
        }

        var dates = new PosixTransitionDate[2];
        for (var index = 0; index < 2; index++)
        {
            if (At(text, position) != ',')
            {
                throw Invalid($"Missing transition rules in TZ string: {Repr(text)}");
            }

            position++;
            if (!ParseTransitionDate(text, ref position, out dates[index]))
            {
                throw Invalid($"Malformed transition rule in TZ string: {Repr(text)}");
            }
        }

        return position == text.Length
            ? new PosixTzRule(standard, dst, dates[0], dates[1])
            : throw Invalid($"Extraneous characters at end of TZ string: {Repr(text)}");
    }

    /// <summary><c>find_tzrule_ttinfo</c>: the offset at a local timestamp of <paramref name="year"/> with <paramref name="fold"/>.</summary>
    public long OffsetAtLocal(long timestamp, int fold, int year)
    {
        if (StandardOnly)
        {
            return StandardOffset;
        }

        var start = _start.YearToTimestamp(year);
        var end = _end.YearToTimestamp(year);
        if ((fold == 1) == (DstDiff >= 0))
        {
            end -= DstDiff;
        }
        else
        {
            start += DstDiff;
        }

        return InDst(timestamp, start, end) ? DstOffset : StandardOffset;
    }

    /// <summary><c>find_tzrule_ttinfo_fromutc</c>: the offset at a UTC timestamp of <paramref name="year"/>, and whether it lies in a fold.</summary>
    public (long Offset, bool Fold) OffsetAtUtc(long timestamp, int year)
    {
        if (StandardOnly)
        {
            return (StandardOffset, false);
        }

        var start = _start.YearToTimestamp(year) - StandardOffset;
        var end = _end.YearToTimestamp(year) - DstOffset;
        var isDst = InDst(timestamp, start, end);
        var (ambiguousStart, ambiguousEnd) = DstDiff > 0 ? (end, end + DstDiff) : (start, start - DstDiff);
        return (isDst ? DstOffset : StandardOffset, timestamp >= ambiguousStart && timestamp < ambiguousEnd);
    }

    private static bool InDst(long timestamp, long start, long end)
        => start < end ? timestamp >= start && timestamp < end : timestamp < end || timestamp >= start;

    private static char At(string text, int position) => position < text.Length ? text[position] : '\0';

    private static EngineValueException Invalid(string message) => new(message, nameof(message));

    // repr() of the footer bytes object (ASCII in every real TZif footer).
    private static string Repr(string text) => "b" + EngineRepr.StrRepr(text);

    // parse_abbr: "<" alphanumerics, "+" and "-" ">", or one or more ASCII letters.
    private static bool ParseAbbreviation(string text, ref int position)
    {
        var cursor = position;
        if (At(text, cursor) == '<')
        {
            cursor++;
            while (At(text, cursor) != '>')
            {
                if (!char.IsAsciiLetterOrDigit(At(text, cursor)) && At(text, cursor) is not ('+' or '-'))
                {
                    return false;
                }

                cursor++;
            }

            cursor++;
        }
        else
        {
            while (char.IsAsciiLetter(At(text, cursor)))
            {
                cursor++;
            }

            if (cursor == position)
            {
                return false;
            }
        }

        position = cursor;
        return true;
    }

    // parse_tz_delta: a transition time within 24 hours, negated (a positive POSIX offset is west of Greenwich).
    private static bool ParseOffset(string text, ref int position, out long seconds)
    {
        seconds = 0;
        if (!ParseTransitionTime(text, ref position, out var hours, out var minutes, out var secondsPart) || hours is > 24 or < -24)
        {
            return false;
        }

        seconds = -((hours * 3600L) + (minutes * 60L) + secondsPart);
        return true;
    }

    // parse_transition_rule: Mm.n.d, Jn or n, each optionally followed by /time.
    private static bool ParseTransitionDate(string text, ref int position, out PosixTransitionDate date)
    {
        date = default;
        var cursor = position;
        int month = 0, week = 0, day;
        var calendar = At(text, cursor) == 'M';
        var julian = false;
        if (calendar)
        {
            cursor++;
            if (!ParseDigits(text, ref cursor, 1, 2, out month) || At(text, cursor++) != '.'
                || !ParseDigits(text, ref cursor, 1, 1, out week) || At(text, cursor++) != '.'
                || !ParseDigits(text, ref cursor, 1, 1, out day))
            {
                return false;
            }
        }
        else
        {
            julian = At(text, cursor) == 'J';
            cursor += julian ? 1 : 0;
            if (!ParseDigits(text, ref cursor, 1, 3, out day))
            {
                return false;
            }
        }

        int hour = 2, minute = 0, second = 0;
        if (At(text, cursor) == '/')
        {
            cursor++;
            if (!ParseTransitionTime(text, ref cursor, out hour, out minute, out second))
            {
                return false;
            }
        }

        var valid = calendar
            ? month is >= 1 and <= 12 && week is >= 1 and <= 5 && day is >= 0 and <= 6
            : day >= (julian ? 1 : 0) && day <= 365;
        if (!valid || hour is < -167 or > 167)
        {
            return false;
        }

        date = new PosixTransitionDate(calendar, julian, month, week, day, hour, minute, second);
        position = cursor;
        return true;
    }

    // parse_transition_time: [+-]h[h[h]][:mm[:ss]], the sign applied to every part.
    private static bool ParseTransitionTime(string text, ref int position, out int hour, out int minute, out int second)
    {
        minute = 0;
        second = 0;
        var cursor = position;
        var sign = 1;
        if (At(text, cursor) is '-' or '+')
        {
            sign = At(text, cursor) == '-' ? -1 : 1;
            cursor++;
        }

        if (!ParseDigits(text, ref cursor, 1, 3, out hour))
        {
            return false;
        }

        hour *= sign;
        if (At(text, cursor) == ':')
        {
            cursor++;
            if (!ParseDigits(text, ref cursor, 2, 2, out minute))
            {
                return false;
            }

            minute *= sign;
            if (At(text, cursor) == ':')
            {
                cursor++;
                if (!ParseDigits(text, ref cursor, 2, 2, out second))
                {
                    return false;
                }

                second *= sign;
            }
        }

        position = cursor;
        return true;
    }

    // parse_digits: between min and max ASCII digits.
    private static bool ParseDigits(string text, ref int position, int min, int max, out int value)
    {
        value = 0;
        for (var index = 0; index < max; index++, position++)
        {
            if (!char.IsAsciiDigit(At(text, position)))
            {
                return index >= min;
            }

            value = (value * 10) + (text[position] - '0');
        }

        return true;
    }
}
