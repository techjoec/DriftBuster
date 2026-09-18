using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Text for the values <see cref="EngineJson"/> produces, shaped like Python's <c>str()</c> and <c>repr()</c> but with JSON's
/// <c>null</c> and <c>true</c>/<c>false</c>: integers, floats in the shortest round-trip form with Python's fixed/exponent switch,
/// strings quoted and escaped as <c>str.__repr__</c> does, bytes (<see cref="byte"/> arrays) as <c>b'...'</c>, and lists, tuples
/// (<see cref="object"/> arrays) and dicts spelled with their elements' reprs.
/// </summary>
public static class EngineRepr
{
    /// <summary><c>str(value)</c>: the string itself for a str, otherwise <see cref="Repr"/>.</summary>
    public static string Str(object? value) => value is string text ? text : Repr(value);

    /// <summary>
    /// <c>repr(value)</c> for the JSON value domain. Containers are walked on an explicit stack, so a list nested as
    /// deep as <see cref="EngineJson.MaxNestingDepth"/> is spelled out without touching the thread's stack.
    /// </summary>
    public static string Repr(object? value)
    {
        var builder = new StringBuilder();
        var open = new Stack<ContainerCursor>();
        Append(builder, value, open);
        while (open.Count > 0)
        {
            var cursor = open.Peek();
            if (cursor.TryAdvance(builder, out var item))
            {
                Append(builder, item, open);
            }
            else
            {
                builder.Append(cursor.Closer);
                open.Pop();
            }
        }

        return builder.ToString();
    }

    // Appends a scalar's repr, or a container's opening bracket with its cursor pushed for the loop above to drain.
    private static void Append(StringBuilder builder, object? value, Stack<ContainerCursor> open)
    {
        switch (value)
        {
            case List<object?> list:
                builder.Append('[');
                open.Push(new ContainerCursor(list, "]"));
                return;
            case object?[] tuple:
                // A one-item tuple keeps its trailing comma: (1,).
                builder.Append('(');
                open.Push(new ContainerCursor(tuple, tuple.Length == 1 ? ",)" : ")"));
                return;
            case OrderedDictionary<string, object?> dict:
                builder.Append('{');
                open.Push(new ContainerCursor(dict, "}"));
                return;
            default:
                builder.Append(ScalarRepr(value));
                return;
        }
    }

    private static string ScalarRepr(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        BigInteger number => number.ToString(CultureInfo.InvariantCulture),
        double number => Float(number),
        string text => StrRepr(text),
        byte[] bytes => BytesRepr(bytes),
        _ => throw new ArgumentException($"No repr for {value.GetType()}", nameof(value)),
    };

    /// <summary>
    /// <c>repr(bytes)</c> (a <see cref="byte"/> array): <c>b'...'</c>, double quotes when the bytes hold a single quote and no double
    /// quote; backslash, the quote, tab, line feed and carriage return escaped, every other byte outside space to "~" as <c>\xhh</c>.
    /// </summary>
    public static string BytesRepr(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var quote = Array.IndexOf(bytes, (byte)'\'') >= 0 && Array.IndexOf(bytes, (byte)'"') < 0 ? '"' : '\'';
        var builder = new StringBuilder(bytes.Length + 3).Append('b').Append(quote);
        foreach (var value in bytes)
        {
            var ch = (char)value;
            if (ch == quote || ch == '\\')
            {
                builder.Append('\\').Append(ch);
            }
            else if (ch is '\t' or '\n' or '\r')
            {
                builder.Append(ch switch { '\t' => "\\t", '\n' => "\\n", _ => "\\r" });
            }
            else if (value is < 0x20 or >= 0x7F)
            {
                builder.Append("\\x").Append(Convert.ToHexStringLower([value]));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.Append(quote).ToString();
    }

    /// <summary>One open list, tuple or dict: writes the ", " separator and, for a dict, the <c>'key': </c> prefix of each item.</summary>
    private sealed class ContainerCursor
    {
        private readonly IEnumerator<object?>? _items;
        private readonly IEnumerator<KeyValuePair<string, object?>>? _pairs;
        private bool _started;

        public ContainerCursor(IEnumerable<object?> items, string closer)
        {
            _items = items.GetEnumerator();
            Closer = closer;
        }

        public ContainerCursor(OrderedDictionary<string, object?> dict, string closer)
        {
            _pairs = dict.GetEnumerator();
            Closer = closer;
        }

        public string Closer { get; }

        public bool TryAdvance(StringBuilder builder, out object? item)
        {
            item = null;
            var moved = _items?.MoveNext() ?? _pairs!.MoveNext();
            if (!moved)
            {
                return false;
            }

            if (_started)
            {
                builder.Append(", ");
            }

            _started = true;
            if (_items is not null)
            {
                item = _items.Current;
            }
            else
            {
                builder.Append(StrRepr(_pairs!.Current.Key)).Append(": ");
                item = _pairs.Current.Value;
            }

            return true;
        }
    }

    /// <summary>
    /// <c>repr(float)</c>: the shortest digit string that round-trips, laid out as <c>float_repr_style = 'short'</c>
    /// does: exponent form (<c>1e+16</c>, <c>1.5e-05</c>, two-digit signed exponent) when the decimal point would
    /// sit at or before position -4 or after position 16, otherwise fixed form with at least one fractional digit
    /// (<c>1000000000000000.0</c>, <c>0.0001</c>); <c>nan</c>, <c>inf</c> and <c>-inf</c> spelled in lower case.
    /// </summary>
    public static string Float(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        // "R" is the shortest round-trip form on .NET Core 3.0+, as Python's repr is; only its layout differs.
        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = roundTrip.StartsWith('-');
        var unsigned = negative ? roundTrip[1..] : roundTrip;
        var exponentAt = unsigned.IndexOf('E');
        var mantissa = exponentAt < 0 ? unsigned : unsigned[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(unsigned[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dotAt = mantissa.IndexOf('.');
        var digits = dotAt < 0 ? mantissa : mantissa.Remove(dotAt, 1);
        var decimalPoint = (dotAt < 0 ? mantissa.Length : dotAt) + exponent;

        var leading = 0;
        while (leading < digits.Length && digits[leading] == '0')
        {
            leading++;
        }

        digits = digits[leading..].TrimEnd('0');
        decimalPoint -= leading;
        var sign = negative ? "-" : string.Empty;
        return digits.Length == 0 ? sign + "0.0" : sign + Layout(digits, decimalPoint);
    }

    // value = 0.<digits> * 10^decimalPoint with digits[0] != '0'.
    private static string Layout(string digits, int decimalPoint)
    {
        if (decimalPoint is <= -4 or > 16)
        {
            var fraction = digits.Length > 1 ? "." + digits[1..] : string.Empty;
            var exponent = decimalPoint - 1;
            return digits[0] + fraction + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }

        if (decimalPoint <= 0)
        {
            return "0." + new string('0', -decimalPoint) + digits;
        }

        if (decimalPoint >= digits.Length)
        {
            return digits + new string('0', decimalPoint - digits.Length) + ".0";
        }

        return digits[..decimalPoint] + "." + digits[decimalPoint..];
    }

    /// <summary>
    /// <c>str.__repr__</c>: single quotes unless the text holds a single quote and no double quote; backslash and
    /// the chosen quote escaped; \t, \n and \r by name; every other non-printable code point as \xNN, \uNNNN or
    /// \UNNNNNNNN. Printable is <see cref="EngineText.IsPrintable"/> (Python's Unicode 15.1 <c>str.isprintable()</c>).
    /// </summary>
    public static string StrRepr(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder(text.Length + 2).Append(quote);
        var offset = 0;
        while (offset < text.Length)
        {
            // An unpaired surrogate is a code point of its own to Python (category Cs, escaped as \udXXX).
            var codePoint = char.IsSurrogatePair(text, offset) ? char.ConvertToUtf32(text, offset) : text[offset];
            offset += codePoint > 0xFFFF ? 2 : 1;
            AppendCodePoint(builder, codePoint, quote);
        }

        return builder.Append(quote).ToString();
    }

    private static void AppendCodePoint(StringBuilder builder, int codePoint, char quote)
    {
        switch (codePoint)
        {
            case '\\':
                builder.Append("\\\\");
                return;
            case '\t':
                builder.Append("\\t");
                return;
            case '\n':
                builder.Append("\\n");
                return;
            case '\r':
                builder.Append("\\r");
                return;
            default:
                break;
        }

        if (codePoint == quote)
        {
            builder.Append('\\').Append(quote);
        }
        else if (EngineText.IsPrintable(codePoint))
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }
        else if (codePoint < 0x100)
        {
            builder.Append("\\x").Append(codePoint.ToString("x2", CultureInfo.InvariantCulture));
        }
        else if (codePoint < 0x10000)
        {
            builder.Append("\\u").Append(codePoint.ToString("x4", CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append("\\U").Append(codePoint.ToString("x8", CultureInfo.InvariantCulture));
        }
    }

}
