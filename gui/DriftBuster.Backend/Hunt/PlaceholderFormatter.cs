using System.Globalization;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Hunt;

/// <summary>
/// <c>template.format(token_name=name)</c> as CPython 3.13's <c>str.format</c> runs it (<c>Objects/stringlib/unicode_format.h</c>
/// and the str branch of <c>Python/formatter_unicode.c</c>, PSF License) with one keyword argument holding a str.
/// </summary>
/// <remarks>
/// Python's exceptions map to: <c>KeyError</c> (an unknown field name) <see cref="KeyNotFoundException"/>, <c>ValueError</c>
/// <see cref="FormatException"/>, <c>IndexError</c> <see cref="StrFormatIndexException"/>, <c>TypeError</c>
/// <see cref="InvalidCastException"/> and <c>AttributeError</c> <see cref="MissingMemberException"/>, each with Python's
/// message. An attribute that <c>str</c> does have (<c>{token_name.upper}</c>) formats a bound method or type object whose
/// text carries a memory address in Python; that is not reproduced and raises <see cref="NotSupportedException"/>.
/// </remarks>
internal static class PlaceholderFormatter
{
    private const int RecursionDepth = 2;

    private static readonly HashSet<string> StrAttributes = new(StringComparer.Ordinal)
    {
        "__add__", "__class__", "__contains__", "__delattr__", "__dir__", "__doc__", "__eq__", "__format__", "__ge__",
        "__getattribute__", "__getitem__", "__getnewargs__", "__getstate__", "__gt__", "__hash__", "__init__",
        "__init_subclass__", "__iter__", "__le__", "__len__", "__lt__", "__mod__", "__mul__", "__ne__", "__new__",
        "__reduce__", "__reduce_ex__", "__repr__", "__rmod__", "__rmul__", "__setattr__", "__sizeof__", "__str__",
        "__subclasshook__", "capitalize", "casefold", "center", "count", "encode", "endswith", "expandtabs", "find", "format",
        "format_map", "index", "isalnum", "isalpha", "isascii", "isdecimal", "isdigit", "isidentifier", "islower",
        "isnumeric", "isprintable", "isspace", "istitle", "isupper", "join", "ljust", "lower", "lstrip", "maketrans",
        "partition", "removeprefix", "removesuffix", "replace", "rfind", "rindex", "rjust", "rpartition", "rsplit", "rstrip",
        "split", "splitlines", "startswith", "strip", "swapcase", "title", "translate", "upper", "zfill",
    };

    public static string Format(string template, string tokenName)
    {
        var autoNumber = new AutoNumber();
        return ReTokenizer.Text(BuildString(ReTokenizer.CodePoints(template), ReTokenizer.CodePoints(tokenName), RecursionDepth, autoNumber));
    }

    private sealed class AutoNumber
    {
        public int State { get; set; }

        public int Next { get; set; }
    }

    private static List<int> BuildString(int[] input, int[] value, int depth, AutoNumber autoNumber)
    {
        if (depth <= 0)
        {
            throw new FormatException("Max string recursion exceeded");
        }

        var output = new List<int>();
        var position = 0;
        while (position < input.Length)
        {
            var start = position;
            var brace = 0;
            while (position < input.Length)
            {
                brace = input[position++];
                if (brace is '{' or '}')
                {
                    break;
                }

                brace = 0;
            }

            var atEnd = position >= input.Length;
            var length = position - start;
            if (brace == '}' && (atEnd || input[position] != '}'))
            {
                throw new FormatException("Single '}' encountered in format string");
            }

            if (atEnd && brace == '{')
            {
                throw new FormatException("Single '{' encountered in format string");
            }

            var markup = brace != 0;
            if (markup && input[position] == brace)
            {
                position++;
                markup = false;
            }
            else if (markup)
            {
                length--;
            }

            output.AddRange(input.AsSpan(start, length));
            if (markup)
            {
                var field = ParseField(input, ref position);
                output.AddRange(OutputMarkup(input, field, value, depth, autoNumber));
            }
        }

        return output;
    }

    private readonly record struct Field(int NameStart, int NameEnd, int SpecStart, int SpecEnd, bool HasSpec, bool SpecNeedsExpanding, int Conversion);

    // parse_field: position is just past the opening brace and ends just past the closing one.
    private static Field ParseField(int[] input, ref int position)
    {
        var nameStart = position;
        var c = 0;
        while (position < input.Length)
        {
            c = input[position++];
            if (c == '{')
            {
                throw new FormatException("unexpected '{' in field name");
            }

            if (c == '[')
            {
                while (position < input.Length && input[position] != ']')
                {
                    position++;
                }

                continue;
            }

            if (c is '}' or ':' or '!')
            {
                break;
            }
        }

        var nameEnd = position - 1;
        if (c is not ('!' or ':'))
        {
            return c == '}' ? new Field(nameStart, nameEnd, 0, 0, false, false, 0)
                : throw new FormatException("expected '}' before end of string");
        }

        var conversion = 0;
        if (c == '!')
        {
            if (position >= input.Length)
            {
                throw new FormatException("end of string while looking for conversion specifier");
            }

            conversion = input[position++];
            if (position < input.Length)
            {
                var after = input[position++];
                if (after == '}')
                {
                    return new Field(nameStart, nameEnd, 0, 0, false, false, conversion);
                }

                if (after != ':')
                {
                    throw new FormatException("expected ':' after conversion specifier");
                }
            }
        }

        return ParseSpec(input, ref position, nameStart, nameEnd, conversion);
    }

    private static Field ParseSpec(int[] input, ref int position, int nameStart, int nameEnd, int conversion)
    {
        var specStart = position;
        var count = 1;
        var expanding = false;
        while (position < input.Length)
        {
            var c = input[position++];
            if (c == '{')
            {
                expanding = true;
                count++;
            }
            else if (c == '}' && --count == 0)
            {
                return new Field(nameStart, nameEnd, specStart, position - 1, true, expanding, conversion);
            }
        }

        throw new FormatException("unmatched '{' in format spec");
    }

    private static List<int> OutputMarkup(int[] input, Field field, int[] value, int depth, AutoNumber autoNumber)
    {
        var obj = FieldObject(input[field.NameStart..field.NameEnd], value, autoNumber);
        obj = field.Conversion switch
        {
            0 or 's' => obj,
            'r' => ReTokenizer.CodePoints(EngineRepr.StrRepr(ReTokenizer.Text(obj))),
            'a' => Ascii(ReTokenizer.CodePoints(EngineRepr.StrRepr(ReTokenizer.Text(obj)))),
            > 32 and < 127 => throw new FormatException($"Unknown conversion specifier {(char)field.Conversion}"),
            _ => throw new FormatException($"Unknown conversion specifier \\x{field.Conversion:x}"),
        };
        var spec = !field.HasSpec ? []
            : field.SpecNeedsExpanding ? [.. BuildString(input[field.SpecStart..field.SpecEnd], value, depth - 1, autoNumber)]
            : input[field.SpecStart..field.SpecEnd];
        return StrFormatSpec.Apply(obj, spec);
    }

    // get_field_object: the keyword lookup or positional index, then each .attribute and [index].
    private static int[] FieldObject(int[] name, int[] value, AutoNumber autoNumber)
    {
        var firstEnd = 0;
        while (firstEnd < name.Length && name[firstEnd] is not ('.' or '['))
        {
            firstEnd++;
        }

        var first = name[..firstEnd];
        var index = DecimalValue(first);
        if (first.Length == 0 || index is not null)
        {
            index = NumericField(first, index, autoNumber);
            throw new StrFormatIndexException($"Replacement index {index} out of range for positional args tuple");
        }

        if (!string.Equals(ReTokenizer.Text(first), "token_name", StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(EngineRepr.StrRepr(ReTokenizer.Text(first)));
        }

        var obj = value;
        var position = firstEnd;
        while (position < name.Length)
        {
            obj = Lookup(name, ref position, obj);
        }

        return obj;
    }

    private static long NumericField(int[] first, long? index, AutoNumber autoNumber)
    {
        const int init = 0;
        const int auto = 1;
        const int manual = 2;
        var empty = first.Length == 0;
        if (autoNumber.State == init)
        {
            autoNumber.State = empty ? auto : manual;
        }

        if (autoNumber.State == manual && empty)
        {
            throw new FormatException("cannot switch from manual field specification to automatic field numbering");
        }

        if (autoNumber.State == auto && !empty)
        {
            throw new FormatException("cannot switch from automatic field numbering to manual field specification");
        }

        return empty ? autoNumber.Next++ : index!.Value;
    }

    // FieldNameIterator_next and the lookup it drives, on a str object.
    private static int[] Lookup(int[] name, ref int position, int[] obj)
    {
        var kind = name[position++];
        if (kind is not ('.' or '['))
        {
            throw new FormatException("Only '.' or '[' may follow ']' in format field specifier");
        }

        var start = position;
        int end;
        if (kind == '.')
        {
            while (position < name.Length && name[position] is not ('.' or '['))
            {
                position++;
            }

            end = position;
        }
        else
        {
            while (position < name.Length && name[position] != ']')
            {
                position++;
            }

            if (position >= name.Length)
            {
                throw new FormatException("Missing ']' in format string");
            }

            end = position++;
        }

        var key = name[start..end];
        var index = kind == '[' ? DecimalValue(key) : null;
        if (key.Length == 0)
        {
            throw new FormatException("Empty attribute in format string");
        }

        if (kind == '.')
        {
            var attribute = ReTokenizer.Text(key);
            throw StrAttributes.Contains(attribute)
                ? new NotSupportedException($"placeholder_template attribute access '{attribute}' is not supported")
                : new MissingMemberException($"'str' object has no attribute {EngineRepr.StrRepr(attribute)}");
        }

        return index switch
        {
            null => throw new InvalidCastException("string indices must be integers, not 'str'"),
            _ when index.Value < obj.Length => [obj[index.Value]],
            _ => throw new StrFormatIndexException("string index out of range"),
        };
    }

    // get_integer: a non-empty run of Unicode decimal digits, or null.
    private static long? DecimalValue(int[] digits)
    {
        if (digits.Length == 0)
        {
            return null;
        }

        long accumulator = 0;
        foreach (var code in digits)
        {
            if (!DecimalDigit(code, out var digit))
            {
                return null;
            }

            if (accumulator > (long.MaxValue - digit) / 10)
            {
                throw new FormatException("Too many decimal digits in format string");
            }

            accumulator = (accumulator * 10) + digit;
        }

        return accumulator;
    }

    private static bool DecimalDigit(int code, out int digit)
    {
        digit = -1;
        if (EngineCharacterData.IsSurrogate(code) || code > 0x10FFFF)
        {
            return false;
        }

        digit = EngineUnicode.DecimalValue(code);
        return digit >= 0;
    }

    // PyObject_ASCII: the repr with every non-ASCII code point backslash-escaped.
    private static int[] Ascii(int[] repr)
    {
        var builder = new StringBuilder();
        foreach (var code in repr)
        {
            builder.Append(code switch
            {
                < 0x80 => ((char)code).ToString(),
                < 0x100 => "\\x" + code.ToString("x2", CultureInfo.InvariantCulture),
                < 0x10000 => "\\u" + code.ToString("x4", CultureInfo.InvariantCulture),
                _ => "\\U" + code.ToString("x8", CultureInfo.InvariantCulture),
            });
        }

        return ReTokenizer.CodePoints(builder.ToString());
    }

}
