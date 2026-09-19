using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Conversions over <see cref="EngineJson"/> values (null, bool, int, float, string, list, dict) used by config readers.
/// Wrong kind throws <see cref="ArgumentException"/>, non-numeric text <see cref="FormatException"/>, out of range
/// <see cref="OverflowException"/>, a lookup on a non-object <see cref="InvalidDataException"/>.
/// </summary>
public static class EngineBuiltins
{
    /// <summary>Longest integer text <see cref="Int"/> accepts.</summary>
    private const int MaxIntStringDigits = 4300;

    // Whitespace trimmed around a numeric literal.
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\n', '\v', '\f', '\r'];

    /// <summary>The kind of <paramref name="value"/> as error messages name it, in JSON's terms where the value is a JSON value.</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "null",
        bool => "boolean",
        int or long or BigInteger => "integer",
        double => "number",
        string => "string",
        byte[] => "byte array",
        IDictionary or IReadOnlyDictionary<string, object?> => "object",
        IEnumerable => "array",
        _ => value.GetType().Name,
    };

    /// <summary>False for null, false, zero, empty strings and empty collections; true otherwise.</summary>
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

    /// <summary>Code points of a string (a lone surrogate counts as one), items of a list or tuple, keys of a dict; anything else throws.</summary>
    public static int Len(object? value) => value switch
    {
        string text => CodePointCount(text),
        IReadOnlyDictionary<string, object?> mapping => mapping.Count,
        ICollection collection => collection.Count,
        _ => throw new InvalidDataException($"A value of type '{TypeName(value)}' has no length."),
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

    /// <summary>A key's value on a JSON object (null when absent); any other value throws <see cref="InvalidDataException"/>.</summary>
    public static object? Get(object? value, string key)
    {
        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            return mapping.TryGetValue(key, out var item) ? item : null;
        }

        throw new InvalidDataException($"expected a JSON object, not '{TypeName(value)}'");
    }

    /// <summary>A string's code points, a list's items or a dict's keys; anything else throws.</summary>
    public static IEnumerable<object?> Iterate(object? value) => value switch
    {
        string text => CodePoints(text),
        IReadOnlyDictionary<string, object?> mapping => mapping.Keys.Cast<object?>().ToList(),
        IList list => list.Cast<object?>().ToList(),
        _ => throw NotEnumerable(value),
    };

    private static InvalidDataException NotEnumerable(object? value) => new($"A value of type '{TypeName(value)}' cannot be enumerated.");

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
    /// A copy of a mapping in order, or a mapping built from two-item pairs (a later key wins). Non-pairs and non-string keys
    /// throw <see cref="ArgumentException"/>.
    /// </summary>
    public static OrderedDictionary<string, object?> Dict(object? value, string what = "mapping")
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
            throw NotEnumerable(value);
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
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Element {index} of the {what} sequence is not a key/value pair."));
        }

        var elements = Iterate(item).ToList();
        if (elements.Count != 2)
        {
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Element {index} of the {what} sequence has {elements.Count} items; a key/value pair has 2."));
        }

        if (elements[0] is { } candidate)
        {
            _ = EngineValues.HashKeys.GetHashCode(candidate);
        }

        return elements[0] is string key
            ? (key, elements[1])
            : throw new InvalidDataException($"The {what} keys must be strings, not '{TypeName(elements[0])}'.");
    }

    /// <summary>Bools and ints as is, floats truncated, strings parsed in base 10.</summary>
    public static BigInteger Int(object? value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        int number => number,
        long number => number,
        BigInteger number => number,
        double number when double.IsNaN(number) => throw new InvalidDataException("NaN cannot be converted to an integer."),
        double number when double.IsInfinity(number) => throw new OverflowException("Infinity cannot be converted to an integer."),
        double number => new BigInteger(Math.Truncate(number)),
        string text => ParseInt(text),
        _ => throw new InvalidDataException($"A value of type '{TypeName(value)}' cannot be converted to an integer."),
    };

    // Unicode digits and whitespace are folded to ASCII, then: optional whitespace, sign, digits with single underscores
    // between them, optional whitespace.
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
            throw new FormatException($"The value {TruncateCodePoints(repr, 200)} is not a valid integer.");
        }

        if (digits.Length > MaxIntStringDigits)
        {
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"The value has {digits.Length} digits, more than the {MaxIntStringDigits} an integer may have."));
        }

        var magnitude = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        return body![0] == '-' ? -magnitude : magnitude;
    }

    /// <summary>Bools and ints converted (past the double range throws), floats as is, strings parsed as a decimal literal or inf/infinity/nan.</summary>
    public static double Float(object? value) => value switch
    {
        bool flag => flag ? 1.0 : 0.0,
        int number => number,
        long number => number,
        BigInteger number when double.IsInfinity((double)number) => throw new OverflowException("The integer is too large to convert to a floating-point number."),
        BigInteger number => (double)number,
        double number => number,
        string text => ParseFloat(text),
        _ => throw new InvalidDataException($"A value of type '{TypeName(value)}' cannot be converted to a number."),
    };

    // Unicode digits and whitespace folded to ASCII; underscores only between digits; then a decimal literal or
    // inf/infinity/nan in any case with an optional sign.
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

        throw new FormatException($"The value {EngineRepr.StrRepr(text)} is not a valid number.");
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

    // Null when an underscore is not between two digits.
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

    // ASCII (U+001C-U+001F included) kept, other whitespace becomes ' ', Unicode decimal digits their ASCII digit; null
    // for any other code point.
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
