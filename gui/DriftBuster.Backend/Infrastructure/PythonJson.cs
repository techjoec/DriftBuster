using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>json.loads</c> producing Python-shaped values: a dict becomes an insertion-ordered
/// <see cref="OrderedDictionary{TKey, TValue}"/> where a duplicate key keeps its first slot with the last value, a
/// list becomes <see cref="List{T}"/> of <see cref="object"/>, a str a <see cref="string"/>, an int an
/// <see cref="int"/>, <see cref="long"/> or <see cref="BigInteger"/> (the smallest that holds it), a float a
/// <see cref="double"/>, true/false a <see cref="bool"/> and null a null reference.
/// </summary>
/// <remarks>
/// The grammar is CPython's C scanner in strict mode, as <c>Detection/Plugins/PythonJsonScanner.cs</c> spells it
/// out: whitespace is space, tab, LF and CR; <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> are floats; numbers
/// use ASCII digits with no leading zeros, a lexeme with a fraction or exponent is a float (correctly rounded, so
/// an overflowing exponent gives an infinity as <c>float()</c> does); strings reject raw control characters below
/// U+0020 and keep unpaired surrogate escapes; trailing commas, comments and a leading U+FEFF are rejected;
/// anything but whitespace after the value is extra data. An integer literal of more than
/// <see cref="MaxIntDigits"/> digits fails as CPython's <c>int()</c> conversion limit does, and a container nested
/// deeper than <see cref="MaxNestingDepth"/> fails as the C scanner's <c>RecursionError</c> does. Nesting is kept on
/// an explicit stack, so neither the decode nor a caller walking the result needs the thread's stack.
/// </remarks>
public static class PythonJson
{
    /// <summary><c>sys.get_int_max_str_digits()</c> default: longer integer literals raise <c>ValueError</c>.</summary>
    public const int MaxIntDigits = 4300;

    /// <summary>
    /// The deepest container nesting <c>json.loads</c> decodes. The C scanner enters one recursion level per array or
    /// object, including empty ones, against <c>Py_C_RECURSION_LIMIT</c> (10000 on 64-bit Linux CPython 3.13) less the
    /// levels the interpreter already holds at the call; 9998 nested containers decode and 9999 raise, measured both
    /// from a top-level script and through <c>Detector.scan_file</c> into the registry-live plugin.
    /// </summary>
    public const int MaxNestingDepth = 9998;

    private sealed class Frame(object container)
    {
        public object Container { get; } = container;

        public OrderedDictionary<string, object?>? Dict => Container as OrderedDictionary<string, object?>;

        public string? PendingKey { get; set; }
    }

    /// <summary>Decodes <paramref name="text"/>; false when Python's decoder would raise.</summary>
    public static bool TryLoads(string text, out object? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        if (text.StartsWith('﻿'))
        {
            return false;
        }

        var pos = SkipWhitespace(text, 0);
        var stack = new Stack<Frame>();
        while (true)
        {
            if (!TryBeginValue(text, ref pos, stack, out var completed, out var hasValue))
            {
                return false;
            }

            if (!hasValue)
            {
                continue;
            }

            while (true)
            {
                if (stack.Count == 0)
                {
                    // "Extra data": anything but whitespace after the value fails the whole decode.
                    if (SkipWhitespace(text, pos) != text.Length)
                    {
                        return false;
                    }

                    value = completed;
                    return true;
                }

                var outcome = Attach(text, ref pos, stack, ref completed);
                if (outcome == Outcome.Failed)
                {
                    return false;
                }

                if (outcome == Outcome.NextValue)
                {
                    break;
                }
            }
        }
    }

    private enum Outcome
    {
        Failed,
        Closed,
        NextValue,
    }

    // Parses the value at pos. A non-empty container is pushed and hasValue stays false so the caller parses its
    // first element; an empty container or a scalar is returned as completed.
    private static bool TryBeginValue(string text, ref int pos, Stack<Frame> stack, out object? completed, out bool hasValue)
    {
        completed = null;
        hasValue = false;
        if (pos >= text.Length)
        {
            return false;
        }

        var c = text[pos];
        if ((c is '{' or '[') && stack.Count >= MaxNestingDepth)
        {
            return false;
        }

        if (c == '{')
        {
            var dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            pos = SkipWhitespace(text, pos + 1);
            if (pos < text.Length && text[pos] == '}')
            {
                pos++;
                completed = dict;
                hasValue = true;
                return true;
            }

            if (!TryParseKey(text, ref pos, out var key))
            {
                return false;
            }

            stack.Push(new Frame(dict) { PendingKey = key });
            return true;
        }

        if (c == '[')
        {
            var list = new List<object?>();
            pos = SkipWhitespace(text, pos + 1);
            if (pos < text.Length && text[pos] == ']')
            {
                pos++;
                completed = list;
                hasValue = true;
                return true;
            }

            stack.Push(new Frame(list));
            return true;
        }

        hasValue = TryParseScalar(text, ref pos, out completed);
        return hasValue;
    }

    // Stores the completed value in the innermost container; closes it when its delimiter follows (the container
    // becomes the completed value), or positions pos at the next element after a comma.
    private static Outcome Attach(string text, ref int pos, Stack<Frame> stack, ref object? completed)
    {
        var parent = stack.Peek();
        if (parent.Dict is { } dict)
        {
            dict[parent.PendingKey!] = completed;
        }
        else
        {
            ((List<object?>)parent.Container).Add(completed);
        }

        pos = SkipWhitespace(text, pos);
        if (pos >= text.Length)
        {
            return Outcome.Failed;
        }

        var closer = parent.Dict is null ? ']' : '}';
        if (text[pos] == closer)
        {
            pos++;
            stack.Pop();
            completed = parent.Container;
            return Outcome.Closed;
        }

        if (text[pos] != ',')
        {
            return Outcome.Failed;
        }

        pos = SkipWhitespace(text, pos + 1);
        if (parent.Dict is not null)
        {
            if (!TryParseKey(text, ref pos, out var key))
            {
                return Outcome.Failed;
            }

            parent.PendingKey = key;
        }

        return Outcome.NextValue;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static int SkipWhitespace(string text, int pos)
    {
        while (pos < text.Length && IsWhitespace(text[pos]))
        {
            pos++;
        }

        return pos;
    }

    // "key" ws ':' ws
    private static bool TryParseKey(string text, ref int pos, out string key)
    {
        key = string.Empty;
        if (pos >= text.Length || text[pos] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        if (!TryScanString(text, pos + 1, out pos, builder))
        {
            return false;
        }

        key = builder.ToString();
        pos = SkipWhitespace(text, pos);
        if (pos >= text.Length || text[pos] != ':')
        {
            return false;
        }

        pos = SkipWhitespace(text, pos + 1);
        return true;
    }

    private static bool TryParseScalar(string text, ref int pos, out object? value)
    {
        value = null;
        switch (text[pos])
        {
            case '"':
                var builder = new StringBuilder();
                if (!TryScanString(text, pos + 1, out pos, builder))
                {
                    return false;
                }

                value = builder.ToString();
                return true;
            case 'n':
                return TryLiteral(text, ref pos, "null");
            case 't':
                value = true;
                return TryLiteral(text, ref pos, "true");
            case 'f':
                value = false;
                return TryLiteral(text, ref pos, "false");
            case 'N':
                value = double.NaN;
                return TryLiteral(text, ref pos, "NaN");
            case 'I':
                value = double.PositiveInfinity;
                return TryLiteral(text, ref pos, "Infinity");
            case '-' when TryLiteral(text, ref pos, "-Infinity"):
                value = double.NegativeInfinity;
                return true;
            default:
                return TryParseNumber(text, ref pos, out value);
        }
    }

    private static bool TryLiteral(string text, ref int pos, string literal)
    {
        if (!text.AsSpan(pos).StartsWith(literal, StringComparison.Ordinal))
        {
            return false;
        }

        pos += literal.Length;
        return true;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    // _match_number_unicode: -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)? over ASCII digits, where a '.' not followed
    // by a digit and an exponent without digits end the number instead of failing it.
    private static bool TryParseNumber(string text, ref int pos, out object? value)
    {
        value = null;
        var idx = pos;
        if (idx < text.Length && text[idx] == '-')
        {
            idx++;
        }

        var digitsStart = idx;
        if (!TryMatchIntegerPart(text, ref idx))
        {
            return false;
        }

        var digitsEnd = idx;
        var isFloat = false;
        if (idx < text.Length && text[idx] == '.' && idx + 1 < text.Length && IsAsciiDigit(text[idx + 1]))
        {
            isFloat = true;
            idx += 2;
            while (idx < text.Length && IsAsciiDigit(text[idx]))
            {
                idx++;
            }
        }

        if (idx < text.Length && text[idx] is 'e' or 'E')
        {
            var exponentEnd = MatchExponent(text, idx);
            if (exponentEnd > idx)
            {
                isFloat = true;
                idx = exponentEnd;
            }
        }

        var lexeme = text[pos..idx];
        pos = idx;
        if (isFloat)
        {
            value = double.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }

        if (digitsEnd - digitsStart > MaxIntDigits)
        {
            return false;
        }

        value = ParseInteger(lexeme);
        return true;
    }

    // (0|[1-9][0-9]*)
    private static bool TryMatchIntegerPart(string text, ref int idx)
    {
        if (idx < text.Length && text[idx] is >= '1' and <= '9')
        {
            idx++;
            while (idx < text.Length && IsAsciiDigit(text[idx]))
            {
                idx++;
            }

            return true;
        }

        if (idx < text.Length && text[idx] == '0')
        {
            idx++;
            return true;
        }

        return false;
    }

    /// <summary>The narrowest of int, long and BigInteger that holds <paramref name="lexeme"/>.</summary>
    public static object ParseInteger(string lexeme)
    {
        ArgumentNullException.ThrowIfNull(lexeme);
        if (int.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var small))
        {
            return small;
        }

        if (long.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var wide))
        {
            return wide;
        }

        return BigInteger.Parse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    // Index past "[eE][-+]?[0-9]+" starting at the 'e', or "start" itself when no digit follows (the exponent is
    // then not part of the number).
    private static int MatchExponent(string text, int start)
    {
        var idx = start + 1;
        if (idx < text.Length && text[idx] is '+' or '-')
        {
            idx++;
        }

        while (idx < text.Length && IsAsciiDigit(text[idx]))
        {
            idx++;
        }

        return IsAsciiDigit(text[idx - 1]) ? idx : start;
    }

    // scanstring_unicode with strict=True: "start" is just past the opening quote; "end" lands just past the closing one.
    private static bool TryScanString(string text, int start, out int end, StringBuilder builder)
    {
        var i = start;
        end = start;
        while (true)
        {
            if (i >= text.Length)
            {
                return false;
            }

            var c = text[i];
            if (c == '"')
            {
                end = i + 1;
                return true;
            }

            if (c <= '\x1f')
            {
                return false;
            }

            if (c != '\\')
            {
                builder.Append(c);
                i++;
                continue;
            }

            if (!TryScanEscape(text, i, out i, builder))
            {
                return false;
            }
        }
    }

    // "i" is at the backslash; "next" lands after the escape. A high surrogate escape followed by a low surrogate
    // escape is one code point; followed by any other \uXXXX it stays alone and the second escape is rescanned.
    private static bool TryScanEscape(string text, int i, out int next, StringBuilder builder)
    {
        next = i;
        var escapeAt = i + 1;
        if (escapeAt >= text.Length)
        {
            return false;
        }

        var e = text[escapeAt];
        if (e != 'u')
        {
            var mapped = e switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => '\0',
            };
            if (mapped == '\0')
            {
                return false;
            }

            builder.Append(mapped);
            next = escapeAt + 1;
            return true;
        }

        var hexStart = escapeAt + 1;
        var hexEnd = hexStart + 4;
        if (hexEnd >= text.Length || !TryHex4(text, hexStart, out var code))
        {
            return false;
        }

        next = hexEnd;
        if (char.IsHighSurrogate(code) && hexEnd + 6 < text.Length && text[hexEnd] == '\\' && text[hexEnd + 1] == 'u')
        {
            if (!TryHex4(text, hexEnd + 2, out var code2))
            {
                return false;
            }

            if (char.IsLowSurrogate(code2))
            {
                builder.Append(code).Append(code2);
                next = hexEnd + 6;
                return true;
            }
        }

        builder.Append(code);
        return true;
    }

    private static bool TryHex4(string text, int start, out char code)
    {
        var value = 0;
        for (var index = start; index < start + 4; index++)
        {
            var digit = text[index] switch
            {
                >= '0' and <= '9' => text[index] - '0',
                >= 'a' and <= 'f' => text[index] - 'a' + 10,
                >= 'A' and <= 'F' => text[index] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                code = '\0';
                return false;
            }

            value = (value << 4) | digit;
        }

        code = (char)value;
        return true;
    }
}
