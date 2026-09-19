using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// JSON decode into plain CLR values: objects as insertion-ordered <see cref="OrderedDictionary{TKey, TValue}"/> (a duplicate
/// key keeps its first slot, last value), arrays as <see cref="List{T}"/>, integers as the narrowest of int/long/BigInteger,
/// fractions and exponents as double.
/// </summary>
/// <remarks>
/// Accepts <c>NaN</c>, <c>Infinity</c>, <c>-Infinity</c> and unpaired surrogate escapes; rejects comments, trailing commas, a
/// leading BOM, raw control characters in strings and trailing data. Integers over <see cref="MaxIntDigits"/> digits and nesting
/// past <see cref="MaxNestingDepth"/> are rejected so hostile input cannot cause unbounded work. Nesting uses an explicit stack.
/// </remarks>
public static class EngineJson
{
    /// <summary>The longest integer literal that decodes; a longer one is rejected.</summary>
    public const int MaxIntDigits = 4300;

    /// <summary>Deepest nesting accepted, one level per array or object.</summary>
    public const int MaxNestingDepth = 9998;

    // All NaN literals share one boxed double, so EngineValues' identity-first comparison finds a decoded NaN equal to itself.
    private static readonly object NaN = double.NaN;
    private static readonly object PositiveInfinity = double.PositiveInfinity;
    private static readonly object NegativeInfinity = double.NegativeInfinity;

    private sealed class Frame(object container)
    {
        public object Container { get; } = container;

        public OrderedDictionary<string, object?>? Dict => Container as OrderedDictionary<string, object?>;

        public string? PendingKey { get; set; }
    }

    /// <summary>Decodes <paramref name="text"/>; false when it is not a JSON document.</summary>
    public static bool TryLoads(string text, out object? value) => TryLoads(text, out value, out _);

    /// <summary>
    /// <see cref="TryLoads(string, out object?)"/>, but a document past a decoder limit (<see cref="MaxIntDigits"/>,
    /// <see cref="MaxNestingDepth"/>) throws <see cref="InvalidDataException"/> instead of returning false.
    /// </summary>
    public static bool TryLoadsOrRaiseLimits(string text, out object? value)
    {
        if (TryLoads(text, out value, out var limit))
        {
            return true;
        }

        return limit is null ? false : throw limit;
    }

    private static bool TryLoads(string text, out object? value, out Exception? limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        limit = null;
        if (text.StartsWith('﻿'))
        {
            return false;
        }

        var pos = SkipWhitespace(text, 0);
        var stack = new Stack<Frame>();
        while (true)
        {
            if (!TryBeginValue(text, ref pos, stack, out var completed, out var hasValue, ref limit))
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
    private static bool TryBeginValue(string text, ref int pos, Stack<Frame> stack, out object? completed, out bool hasValue, ref Exception? limit)
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
            limit = new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"The JSON document is nested more than {MaxNestingDepth} levels deep."));
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

        hasValue = TryParseScalar(text, ref pos, out completed, ref limit);
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

    private static bool TryParseScalar(string text, ref int pos, out object? value, ref Exception? limit)
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
                value = NaN;
                return TryLiteral(text, ref pos, "NaN");
            case 'I':
                value = PositiveInfinity;
                return TryLiteral(text, ref pos, "Infinity");
            case '-' when TryLiteral(text, ref pos, "-Infinity"):
                value = NegativeInfinity;
                return true;
            default:
                return TryParseNumber(text, ref pos, out value, ref limit);
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

    // -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)? over ASCII digits; a '.' or exponent without digits ends the number
    // rather than failing it.
    private static bool TryParseNumber(string text, ref int pos, out object? value, ref Exception? limit)
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
            limit = new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"The JSON document holds an integer of {digitsEnd - digitsStart} digits, more than the {MaxIntDigits} allowed."));
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

    // Strict string scan: "start" is just past the opening quote, "end" just past the closing one.
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
