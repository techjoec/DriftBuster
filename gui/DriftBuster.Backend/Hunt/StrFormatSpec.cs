using System.Globalization;
using System.Text;

using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Hunt;

/// <summary>
/// <c>format(value, spec)</c> for a str value: CPython 3.13's <c>parse_internal_render_format_spec</c> (default type <c>s</c>,
/// default alignment <c>&lt;</c>) and <c>format_string_internal</c> over code points. Errors are <see cref="FormatException"/>
/// (Python's <c>ValueError</c>) with Python's messages.
/// </summary>
internal static class StrFormatSpec
{
    public static List<int> Apply(int[] value, int[] spec)
    {
        if (spec.Length == 0)
        {
            return [.. value];
        }

        var parsed = Parse(spec);
        if (parsed.Type != 's')
        {
            throw new FormatException(parsed.Type is > 32 and < 128
                ? $"Unknown format code '{(char)parsed.Type}' for object of type 'str'"
                : $"Unknown format code '\\x{parsed.Type:x}' for object of type 'str'");
        }

        Validate(parsed);
        var length = (long)value.Length;
        if (parsed.Precision >= 0 && length >= parsed.Precision)
        {
            length = parsed.Precision;
        }

        var total = parsed.Width >= 0 && parsed.Width > length ? parsed.Width : length;
        var left = parsed.Align switch
        {
            '>' => total - length,
            '^' => (total - length) / 2,
            _ => 0,
        };
        var output = new List<int>();
        output.AddRange(Enumerable.Repeat(parsed.Fill, (int)left));
        output.AddRange(value.AsSpan(0, (int)length));
        output.AddRange(Enumerable.Repeat(parsed.Fill, (int)(total - length - left)));
        return output;
    }

    private readonly record struct Spec(int Fill, int Align, int Sign, bool NoNegativeZero, bool Alternate, long Width, int Separator, long Precision, int Type);

    private static Spec Parse(int[] spec)
    {
        var position = 0;
        var fill = (int)' ';
        var align = (int)'<';
        var fillSpecified = false;
        if (spec.Length >= 2 && IsAlignment(spec[1]))
        {
            (fill, align, fillSpecified) = (spec[0], spec[1], true);
            position = 2;
        }
        else if (IsAlignment(spec[0]))
        {
            align = spec[0];
            position = 1;
        }

        var sign = position < spec.Length && spec[position] is ' ' or '+' or '-' ? spec[position++] : 0;
        var noNegativeZero = position < spec.Length && spec[position] == 'z' && ++position > 0;
        var alternate = position < spec.Length && spec[position] == '#' && ++position > 0;
        if (!fillSpecified && position < spec.Length && spec[position] == '0')
        {
            fill = '0';
            position++;
        }

        var width = ReadInteger(spec, ref position, out var widthDigits);
        var separator = 0;
        if (position < spec.Length && spec[position] == ',')
        {
            separator = ',';
            position++;
        }

        if (position < spec.Length && spec[position] == '_')
        {
            separator = separator != 0 ? throw new FormatException("Cannot specify both ',' and '_'.") : '_';
            position++;
        }

        if (position < spec.Length && spec[position] == ',' && separator == '_')
        {
            throw new FormatException("Cannot specify both ',' and '_'.");
        }

        return ParseTail(spec, position, new Spec(fill, align, sign, noNegativeZero, alternate, widthDigits == 0 ? -1 : width, separator, -1, 's'));
    }

    private static Spec ParseTail(int[] spec, int position, Spec parsed)
    {
        if (position < spec.Length && spec[position] == '.')
        {
            position++;
            var precision = ReadInteger(spec, ref position, out var digits);
            if (digits == 0)
            {
                throw new FormatException("Format specifier missing precision");
            }

            parsed = parsed with { Precision = precision };
        }

        if (spec.Length - position > 1)
        {
            throw new FormatException($"Invalid format specifier '{ReTokenizer.Text(spec)}' for object of type 'str'");
        }

        if (spec.Length - position == 1)
        {
            parsed = parsed with { Type = spec[position] };
        }

        var separatorAllowed = parsed.Type is 'd' or 'e' or 'f' or 'g' or 'E' or 'G' or '%' or 'F' or 0
            || (parsed.Separator == '_' && parsed.Type is 'b' or 'o' or 'x' or 'X');
        if (parsed.Separator != 0 && !separatorAllowed)
        {
            var separator = (char)parsed.Separator;
            throw new FormatException(parsed.Type is > 32 and < 128
                ? $"Cannot specify '{separator}' with '{(char)parsed.Type}'."
                : $"Cannot specify '{separator}' with '\\x{parsed.Type:x}'.");
        }

        return parsed;
    }

    private static void Validate(Spec parsed)
    {
        if (parsed.Sign != 0)
        {
            throw new FormatException(parsed.Sign == ' ' ? "Space not allowed in string format specifier" : "Sign not allowed in string format specifier");
        }

        if (parsed.NoNegativeZero)
        {
            throw new FormatException("Negative zero coercion (z) not allowed in string format specifier");
        }

        if (parsed.Alternate)
        {
            throw new FormatException("Alternate form (#) not allowed in string format specifier");
        }

        if (parsed.Align == '=')
        {
            throw new FormatException("'=' alignment not allowed in string format specifier");
        }
    }

    private static bool IsAlignment(int code) => code is '<' or '>' or '=' or '^';

    // get_integer: Unicode decimal digits, overflow past Py_ssize_t refused.
    private static long ReadInteger(int[] spec, ref int position, out int digits)
    {
        long accumulator = 0;
        digits = 0;
        for (; position < spec.Length; position++, digits++)
        {
            var code = spec[position];
            if (PythonCharacterData.IsSurrogate(code) || Rune.GetUnicodeCategory(new Rune(code)) != UnicodeCategory.DecimalDigitNumber)
            {
                break;
            }

            var digit = (long)Rune.GetNumericValue(new Rune(code));
            if (accumulator > (long.MaxValue - digit) / 10)
            {
                throw new FormatException("Too many decimal digits in format string");
            }

            accumulator = (accumulator * 10) + digit;
        }

        return accumulator;
    }
}
