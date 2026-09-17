using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>datetime.fromisoformat</c>, following CPython 3.13's C parser.</summary>
public sealed partial class EngineDateTime
{
    private const int Invalid = int.MinValue;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly int[] FractionCorrection = [100000, 10000, 1000, 100, 10];

    /// <summary>
    /// <c>datetime.fromisoformat(text)</c>. The C parser reads the UTF-8 bytes of the string after a surrogate separator at code point
    /// 7, 8 or 10 is replaced with "T": any single character separates the date (<c>YYYY-MM-DD</c>, <c>YYYYMMDD</c> or an ISO week
    /// date) from the time (<c>HH[:MM[:SS[.f...]]]</c> or the basic form, "." or "," before up to six kept fraction digits), and the
    /// offset is <c>Z</c> or a signed time. Out-of-range fields raise the constructor's <c>ValueError</c> text; anything else
    /// unparsable raises <c>ValueError("Invalid isoformat string: {text!r}")</c>.
    /// </summary>
    public static EngineDateTime FromIsoFormat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = SanitisedUtf8(text) ?? throw InvalidIsoFormat(text);
        var length = bytes.Length - TerminatorPadding;
        var separator = FindSeparator(bytes, length);
        var fields = new IsoFields();
        var rv = ParseIsoDate(bytes, separator, fields);
        if (rv == 0 && length > separator)
        {
            if (separator < 0)
            {
                throw InvalidIsoFormat(text);
            }

            var position = separator + SeparatorWidth(bytes[separator]);
            rv = ParseIsoTime(bytes, position, length - position, fields);
        }

        if (rv < 0)
        {
            throw InvalidIsoFormat(text);
        }

        var tz = rv == 1 ? OffsetZone(fields) : null;
        return Create(fields.Year, fields.Month, fields.Day, fields.Hour, fields.Minute, fields.Second, fields.Microsecond, tz);
    }

    private const int TerminatorPadding = 8;

    private sealed class IsoFields
    {
        public int Year;
        public int Month;
        public int Day;
        public int Hour;
        public int Minute;
        public int Second;
        public int Microsecond;
        public int TzOffset;
        public int TzMicrosecond;
    }

    private static EngineValueException InvalidIsoFormat(string text)
        => new("Invalid isoformat string: " + EngineRepr.StrRepr(text), nameof(text));

    // tzinfo_from_isoformat_results: UTC for a zero second offset (whatever the microseconds), otherwise timezone(timedelta(...)).
    private static EngineFixedOffset OffsetZone(IsoFields fields)
        => fields.TzOffset == 0
            ? EngineFixedOffset.Utc
            : EngineFixedOffset.Create(EngineTimeDelta.FromMicroseconds(((long)fields.TzOffset * 1_000_000) + fields.TzMicrosecond));

    // _sanitize_isoformat_str, then PyUnicode_AsUTF8AndSize: null for fewer than 7 code points or a surrogate left after the
    // separator replacement. The bytes carry zero padding standing in for the C string's terminator.
    private static byte[]? SanitisedUtf8(string text)
    {
        var codePoints = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsSurrogatePair(text, index))
            {
                codePoints.Add(char.ConvertToUtf32(text[index], text[index + 1]));
                index++;
            }
            else
            {
                codePoints.Add(text[index]);
            }
        }

        if (codePoints.Count < 7)
        {
            return null;
        }

        var cleaned = text;
        foreach (var position in (ReadOnlySpan<int>)[7, 8, 10])
        {
            if (position > codePoints.Count)
            {
                break;
            }

            if (position < codePoints.Count && codePoints[position] is >= 0xD800 and <= 0xDFFF)
            {
                codePoints[position] = 'T';
                var builder = new StringBuilder(text.Length);
                foreach (var codePoint in codePoints)
                {
                    builder.Append(codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint));
                }

                cleaned = builder.ToString();
                break;
            }
        }

        try
        {
            var encoded = StrictUtf8.GetBytes(cleaned);
            Array.Resize(ref encoded, encoded.Length + TerminatorPadding);
            return encoded;
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    // The width of the separator character from its first UTF-8 byte.
    private static int SeparatorWidth(byte lead) => (lead & 0x80) == 0
        ? 1
        : (lead & 0xF0) switch
        {
            0xE0 => 3,
            0xF0 => 4,
            _ => 2,
        };

    private static bool IsDigit(byte value) => (uint)(value - '0') < 10;

    // parse_digits: false on the first byte that is not a digit (the position has then moved past it, as in C).
    private static bool ParseDigits(byte[] bytes, ref int position, ref int value, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var digit = (uint)(bytes[position++] - '0');
            if (digit > 9)
            {
                return false;
            }

            value = (value * 10) + (int)digit;
        }

        return true;
    }

    // _find_isoformat_datetime_separator.
    private static int FindSeparator(byte[] bytes, int length)
    {
        if (length == 7)
        {
            return 7;
        }

        if (bytes[4] == '-')
        {
            if (bytes[5] != 'W')
            {
                return 10;
            }

            if (length > 8 && bytes[8] == '-')
            {
                if (length == 9)
                {
                    return -1;
                }

                return length > 10 && IsDigit(bytes[10]) ? 8 : 10;
            }

            return 8;
        }

        if (bytes[4] != 'W')
        {
            return 8;
        }

        var index = 7;
        while (index < length && IsDigit(bytes[index]))
        {
            index++;
        }

        if (index < 9)
        {
            return index;
        }

        return index % 2 == 0 ? 7 : 8;
    }
}
