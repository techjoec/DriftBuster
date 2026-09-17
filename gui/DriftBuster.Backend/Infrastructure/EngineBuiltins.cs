using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python built-ins over the values <see cref="EngineJson"/> produces (<c>None</c>, bool, int, float, str, list, dict), with
/// Python's results and error texts: <c>bool()</c>, <c>int()</c>, <c>float()</c>, <c>iter()</c>, <c>type().__name__</c> and
/// <c>mapping.get</c>. <c>TypeError</c> is <see cref="EngineTypeException"/>, <c>ValueError</c>
/// <see cref="EngineValueException"/>, <c>OverflowError</c> <see cref="OverflowException"/> and <c>AttributeError</c>
/// <see cref="EngineAttributeException"/>.
/// </summary>
public static class EngineBuiltins
{
    /// <summary><c>sys.get_int_max_str_digits()</c> default.</summary>
    private const int MaxIntStringDigits = 4300;

    // Py_ISSPACE: the whitespace PyLong_FromString and float_from_string_inner strip around the ASCII literal.
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\n', '\v', '\f', '\r'];

    /// <summary><c>type(value).__name__</c>.</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        int or long or BigInteger => "int",
        double => "float",
        string => "str",
        byte[] => "bytes",
        object?[] => "tuple",
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

    /// <summary>
    /// <c>len(value)</c>: the code points of a str (an unpaired surrogate is one), the items of a list or tuple, the keys of a dict;
    /// anything else raises <c>TypeError: object of type '&lt;type&gt;' has no len()</c>.
    /// </summary>
    public static int Len(object? value) => value switch
    {
        string text => CodePointCount(text),
        IReadOnlyDictionary<string, object?> mapping => mapping.Count,
        ICollection collection => collection.Count,
        _ => throw new EngineTypeException($"object of type '{TypeName(value)}' has no len()", nameof(value)),
    };

    private static int CodePointCount(string text)
    {
        var count = 0;
        for (var offset = 0; offset < text.Length; offset += char.IsSurrogatePair(text, offset) ? 2 : 1)
        {
            count++;
        }

        return count;
    }

    /// <summary><c>value.get(key)</c> on a mapping; any other value raises <c>AttributeError</c>.</summary>
    public static object? Get(object? value, string key)
    {
        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            return mapping.TryGetValue(key, out var item) ? item : null;
        }

        throw new EngineAttributeException($"expected a JSON object, not '{TypeName(value)}'");
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
        _ => throw new EngineTypeException($"'{TypeName(value)}' object is not iterable", nameof(value)),
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

    /// <summary>
    /// <c>dict(value)</c>: a mapping is copied in its order; a str, list or tuple is read as key/value pairs, each item an iterable of
    /// exactly two elements (<c>TypeError: cannot convert dictionary update sequence element #N to a sequence</c>, <c>ValueError:
    /// dictionary update sequence element #N has length L; 2 is required</c>), a later pair replacing an earlier key; any other value
    /// raises <c>TypeError: '&lt;type&gt;' object is not iterable</c>. A pair whose key is a list or dict raises Python's
    /// <c>unhashable type</c>; one whose key is any other non-str value raises <c>TypeError: {what} keys must be str, not '&lt;type&gt;'</c>,
    /// the typed mappings holding str keys only.
    /// </summary>
    public static OrderedDictionary<string, object?> Dict(object? value, string what = "dict")
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            foreach (var (key, item) in mapping)
            {
                copy[key] = item;
            }

            return copy;
        }

        if (value is not (IList or string))
        {
            throw new EngineTypeException($"'{TypeName(value)}' object is not iterable", nameof(value));
        }

        var index = 0;
        foreach (var item in Iterate(value))
        {
            var (key, element) = DictPair(item, index, what);
            copy[key] = element;
            index++;
        }

        return copy;
    }

    private static (string Key, object? Value) DictPair(object? item, int index, string what)
    {
        if (item is not (string or IList or IReadOnlyDictionary<string, object?>))
        {
            throw new EngineTypeException(
                string.Create(CultureInfo.InvariantCulture, $"cannot convert dictionary update sequence element #{index} to a sequence"),
                nameof(item));
        }

        var elements = Iterate(item).ToList();
        if (elements.Count != 2)
        {
            throw new EngineValueException(
                string.Create(CultureInfo.InvariantCulture, $"dictionary update sequence element #{index} has length {elements.Count}; 2 is required"),
                nameof(item));
        }

        if (elements[0] is { } candidate)
        {
            _ = EngineValues.HashKeys.GetHashCode(candidate);
        }

        return elements[0] is string key
            ? (key, elements[1])
            : throw new EngineTypeException($"{what} keys must be str, not '{TypeName(elements[0])}'", nameof(item));
    }

    /// <summary><c>int(value)</c>: bools and ints as themselves, floats truncated, strs parsed in base 10.</summary>
    public static BigInteger Int(object? value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        int number => number,
        long number => number,
        BigInteger number => number,
        double number when double.IsNaN(number) => throw new EngineValueException("cannot convert float NaN to integer", nameof(value)),
        double number when double.IsInfinity(number) => throw new OverflowException("cannot convert float infinity to integer"),
        double number => new BigInteger(Math.Truncate(number)),
        string text => ParseInt(text),
        _ => throw new EngineTypeException($"int() argument must be a string, a bytes-like object or a real number, not '{TypeName(value)}'", nameof(value)),
    };

    // PyLong_FromUnicodeObject(text, 10): Unicode decimal digits and whitespace become ASCII, then optional whitespace, a sign,
    // digits with single underscores between them, and optional whitespace.
    private static BigInteger ParseInt(string text)
    {
        var ascii = ToAsciiDigitsAndSpaces(text);
        var body = ascii?.Trim(AsciiWhitespace);
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
            var repr = EngineRepr.StrRepr(text);
            throw new EngineValueException($"invalid literal for int() with base 10: {TruncateCodePoints(repr, 200)}", nameof(text));
        }

        if (digits.Length > MaxIntStringDigits)
        {
            throw new EngineValueException(string.Create(
                CultureInfo.InvariantCulture,
                $"Exceeds the limit ({MaxIntStringDigits} digits) for integer string conversion: value has {digits.Length} digits"),
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
        _ => throw new EngineTypeException($"float() argument must be a string or a real number, not '{TypeName(value)}'", nameof(value)),
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

        var body = ascii?.Trim(AsciiWhitespace);
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

        throw new EngineValueException($"could not convert string to float: {EngineRepr.StrRepr(text)}", nameof(text));
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

    // _PyUnicode_TransformDecimalAndSpaceToASCII: a code point below 127 stays as it is (the ASCII separators U+001C to
    // U+001F included), other whitespace becomes ' ', a decimal digit its ASCII digit; null when any other code point is
    // present (the conversion then fails).
    private static string? ToAsciiDigitsAndSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var offset = 0; offset < text.Length;)
        {
            var step = char.IsSurrogatePair(text, offset) ? 2 : 1;
            var ch = text[offset];
            if (step == 1 && ch < 127)
            {
                builder.Append(ch);
            }
            else if (step == 1 && EngineText.IsSpace(ch))
            {
                builder.Append(' ');
            }
            else if (EngineUnicode.DecimalValue(char.ConvertToUtf32(text, offset)) is var digit and >= 0)
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
