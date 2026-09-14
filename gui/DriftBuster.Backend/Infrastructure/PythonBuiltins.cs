using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python built-ins over the values <see cref="PythonJson"/> produces (<c>None</c>, bool, int, float, str, list, dict), with
/// Python's results and error texts: <c>bool()</c>, <c>int()</c>, <c>float()</c>, <c>iter()</c>, <c>type().__name__</c> and
/// <c>mapping.get</c>. <c>TypeError</c> is <see cref="PythonTypeException"/>, <c>ValueError</c>
/// <see cref="PythonValueException"/>, <c>OverflowError</c> <see cref="OverflowException"/> and <c>AttributeError</c>
/// <see cref="InvalidDataException"/>.
/// </summary>
public static class PythonBuiltins
{
    /// <summary><c>sys.get_int_max_str_digits()</c> default.</summary>
    private const int MaxIntStringDigits = 4300;

    /// <summary><c>type(value).__name__</c>.</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        int or long or BigInteger => "int",
        double => "float",
        string => "str",
        IDictionary or IReadOnlyDictionary<string, object?> => "dict",
        IEnumerable => "list",
        _ => value.GetType().Name,
    };

    /// <summary><c>bool(value)</c>.</summary>
    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int number => number != 0,
        long number => number != 0,
        BigInteger number => !number.IsZero,
        double number => number != 0.0,
        ICollection collection => collection.Count > 0,
        _ => true,
    };

    /// <summary><c>value.get(key)</c> on a mapping; any other value raises <c>AttributeError</c>.</summary>
    public static object? Get(object? value, string key)
    {
        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            return mapping.TryGetValue(key, out var item) ? item : null;
        }

        throw new InvalidDataException($"'{TypeName(value)}' object has no attribute 'get'");
    }

    /// <summary>
    /// <c>for item in value</c>: a str yields its code points (an unpaired surrogate is one), a list its items, a dict its keys;
    /// anything else raises <c>TypeError: '&lt;type&gt;' object is not iterable</c>.
    /// </summary>
    public static IEnumerable<object?> Iterate(object? value) => value switch
    {
        string text => CodePoints(text),
        IReadOnlyDictionary<string, object?> mapping => mapping.Keys.Cast<object?>().ToList(),
        IList list => list.Cast<object?>().ToList(),
        _ => throw new PythonTypeException($"'{TypeName(value)}' object is not iterable", nameof(value)),
    };

    private static IEnumerable<object?> CodePoints(string text)
    {
        var items = new List<object?>();
        for (var offset = 0; offset < text.Length;)
        {
            var step = char.IsSurrogatePair(text, offset) ? 2 : 1;
            items.Add(text.Substring(offset, step));
            offset += step;
        }

        return items;
    }

    /// <summary><c>int(value)</c>: bools and ints as themselves, floats truncated, strs parsed in base 10.</summary>
    public static BigInteger Int(object? value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        int number => number,
        long number => number,
        BigInteger number => number,
        double number when double.IsNaN(number) => throw new PythonValueException("cannot convert float NaN to integer", nameof(value)),
        double number when double.IsInfinity(number) => throw new OverflowException("cannot convert float infinity to integer"),
        double number => new BigInteger(Math.Truncate(number)),
        string text => ParseInt(text),
        _ => throw new PythonTypeException($"int() argument must be a string, a bytes-like object or a real number, not '{TypeName(value)}'", nameof(value)),
    };

    // PyLong_FromUnicodeObject(text, 10): Unicode decimal digits and whitespace become ASCII, then optional whitespace, a sign,
    // digits with single underscores between them, and optional whitespace.
    private static BigInteger ParseInt(string text)
    {
        var ascii = ToAsciiDigitsAndSpaces(text);
        var body = ascii?.Trim(' ');
        var digits = new StringBuilder();
        var valid = body is { Length: > 0 };
        if (valid)
        {
            var start = body![0] is '+' or '-' ? 1 : 0;
            var previous = '\0';
            valid = start < body.Length && char.IsAsciiDigit(body[start]);
            for (var index = start; valid && index < body.Length; index++)
            {
                var ch = body[index];
                valid = char.IsAsciiDigit(ch) || (ch == '_' && char.IsAsciiDigit(previous));
                if (char.IsAsciiDigit(ch))
                {
                    digits.Append(ch);
                }

                previous = ch;
            }

            valid = valid && previous != '_';
        }

        if (!valid)
        {
            var repr = PythonRepr.StrRepr(text);
            throw new PythonValueException($"invalid literal for int() with base 10: {TruncateCodePoints(repr, 200)}", nameof(text));
        }

        if (digits.Length > MaxIntStringDigits)
        {
            throw new PythonValueException(string.Create(
                CultureInfo.InvariantCulture,
                $"Exceeds the limit ({MaxIntStringDigits} digits) for integer string conversion: value has {digits.Length} digits; use sys.set_int_max_str_digits() to increase the limit"),
                nameof(text));
        }

        var magnitude = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        return body![0] == '-' ? -magnitude : magnitude;
    }

    /// <summary>
    /// <c>float(value)</c>: bools and ints converted (an int past the float range raises <c>OverflowError</c>), floats as
    /// themselves, strs parsed as <c>PyFloat_FromString</c> does.
    /// </summary>
    public static double Float(object? value) => value switch
    {
        bool flag => flag ? 1.0 : 0.0,
        int number => number,
        long number => number,
        BigInteger number when double.IsInfinity((double)number) => throw new OverflowException("int too large to convert to float"),
        BigInteger number => (double)number,
        double number => number,
        string text => ParseFloat(text),
        _ => throw new PythonTypeException($"float() argument must be a string or a real number, not '{TypeName(value)}'", nameof(value)),
    };

    // PyFloat_FromString: Unicode decimal digits and whitespace become ASCII; an underscore must sit between two digits; then
    // surrounding whitespace is stripped and the rest is a decimal literal or inf, infinity or nan (any case, optional sign).
    private static double ParseFloat(string text)
    {
        var ascii = ToAsciiDigitsAndSpaces(text);
        if (ascii is not null && ascii.Contains('_', StringComparison.Ordinal))
        {
            ascii = RemoveUnderscoresBetweenDigits(ascii);
        }

        var body = ascii?.Trim(' ');
        if (body is { Length: > 0 })
        {
            var unsigned = body[0] is '+' or '-' ? body[1..] : body;
            var negative = body[0] == '-';
            if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
            {
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            }

            if (unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase))
            {
                return double.NaN;
            }

            if (IsDecimalLiteral(unsigned)
                && double.TryParse(body, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw new PythonValueException($"could not convert string to float: {PythonRepr.StrRepr(text)}", nameof(text));
    }

    // digits [. digits] or . digits, then an optional exponent with digits.
    private static bool IsDecimalLiteral(string text)
    {
        var index = 0;
        var mantissaDigits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
            mantissaDigits++;
        }

        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
                mantissaDigits++;
            }
        }

        if (mantissaDigits == 0)
        {
            return false;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-')
            {
                index++;
            }

            var exponentStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            return index > exponentStart && index == text.Length;
        }

        return index == text.Length;
    }

    // _Py_string_to_number_with_underscores: null when an underscore does not sit between two digits.
    private static string? RemoveUnderscoresBetweenDigits(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previous = '\0';
        foreach (var ch in text)
        {
            if (ch == '_')
            {
                if (!char.IsAsciiDigit(previous))
                {
                    return null;
                }
            }
            else
            {
                if (previous == '_' && !char.IsAsciiDigit(ch))
                {
                    return null;
                }

                builder.Append(ch);
            }

            previous = ch;
        }

        return previous == '_' ? null : builder.ToString();
    }

    // _PyUnicode_TransformDecimalAndSpaceToASCII: whitespace becomes ' ', a decimal digit its ASCII digit, other ASCII stays;
    // null when any other non-ASCII code point is present (the conversion then fails).
    private static string? ToAsciiDigitsAndSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var offset = 0; offset < text.Length;)
        {
            var step = char.IsSurrogatePair(text, offset) ? 2 : 1;
            var ch = text[offset];
            if (PythonText.IsSpace(ch))
            {
                builder.Append(' ');
            }
            else if (step == 1 && ch < 0x80)
            {
                builder.Append(ch);
            }
            else if (CharUnicodeInfo.GetDecimalDigitValue(text, offset) is var digit and >= 0)
            {
                builder.Append((char)('0' + digit));
            }
            else
            {
                return null;
            }

            offset += step;
        }

        return builder.ToString();
    }

    private static string TruncateCodePoints(string text, int length)
    {
        var offset = 0;
        for (var count = 0; count < length && offset < text.Length; count++)
        {
            offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
        }

        return text[..offset];
    }
}
