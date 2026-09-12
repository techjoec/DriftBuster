using System.Text;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// A JSON acceptor with exactly the grammar of CPython's <c>json.loads</c> (the C scanner in <c>Modules/_json.c</c>,
/// strict mode): whitespace is space, tab, LF and CR only; the literals <c>NaN</c>, <c>Infinity</c> and
/// <c>-Infinity</c> are accepted; numbers use ASCII digits with no leading zeros; strings reject raw control
/// characters below U+0020, accept the escapes <c>\" \\ \/ \b \f \n \r \t \uXXXX</c>, and keep unpaired surrogate
/// escapes; trailing commas, comments and a leading U+FEFF are rejected; anything after the value but whitespace is
/// "extra data". Nesting is tracked on an explicit stack, so depth is unbounded where Python's recursive decoder
/// raises <c>RecursionError</c> once the nesting exceeds the interpreter's recursion limit, and integers are never
/// converted, so Python's digit limit on <c>int()</c> conversion does not apply either (see
/// tools/parity/expected_divergences.md).
/// </summary>
/// <remarks>Only what the JSON plugin reports is kept: the top-level kind, object keys in first-occurrence order and
/// the kinds of the top-level array items. System.Text.Json is not used because it diverges on the NaN/Infinity
/// literals, on unpaired surrogate escapes and on nesting depth.</remarks>
internal static class PythonJsonScanner
{
    /// <summary>Python types a decoded value can have, in the order of <c>JsonPlugin.TypeNames</c>.</summary>
    internal enum Kind
    {
        Dict,
        List,
        Str,
        Int,
        Float,
        Bool,
        None,
    }

    internal sealed class TopLevel
    {
        private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);

        public Kind Kind { get; init; }

        /// <summary>Object keys as a Python dict orders them: first occurrence wins, later duplicates keep that slot.</summary>
        public List<string> Keys { get; } = [];

        public List<Kind> ItemKinds { get; } = [];

        internal void Record(Frame parent, Kind valueKind)
        {
            if (parent.IsObject)
            {
                if (_seenKeys.Add(parent.PendingKey!))
                {
                    Keys.Add(parent.PendingKey!);
                }
            }
            else
            {
                ItemKinds.Add(valueKind);
            }
        }
    }

    internal sealed class Frame(bool isObject)
    {
        public bool IsObject { get; } = isObject;

        public string? PendingKey { get; set; }
    }

    /// <summary>The top-level description of <paramref name="text"/>, or null when Python's decoder would raise.</summary>
    public static TopLevel? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith('\uFEFF'))
        {
            return null;
        }

        var pos = SkipWhitespace(text, 0);
        var stack = new Stack<Frame>();
        TopLevel? top = null;
        while (true)
        {
            if (!TryBeginValue(text, ref pos, stack, ref top, out var completed))
            {
                return null;
            }

            if (completed is null)
            {
                continue;
            }

            var outcome = CompleteValue(text, ref pos, stack, top!, completed.Value);
            if (outcome == Outcome.Failed)
            {
                return null;
            }

            if (outcome == Outcome.Finished)
            {
                return top;
            }
        }
    }

    private enum Outcome
    {
        Failed,
        Finished,
        NextValue,
    }

    // Parses the value starting at pos. A container that is not empty is pushed and "completed" stays null so the
    // caller parses its first element; an empty container or a scalar reports its kind.
    private static bool TryBeginValue(string text, ref int pos, Stack<Frame> stack, ref TopLevel? top, out Kind? completed)
    {
        completed = null;
        if (pos >= text.Length)
        {
            return false;
        }

        var c = text[pos];
        if (c == '{')
        {
            top ??= new TopLevel { Kind = Kind.Dict };
            pos = SkipWhitespace(text, pos + 1);
            if (pos < text.Length && text[pos] == '}')
            {
                pos++;
                completed = Kind.Dict;
                return true;
            }

            var frame = new Frame(isObject: true);
            if (!TryParseKey(text, ref pos, out var key))
            {
                return false;
            }

            frame.PendingKey = key;
            stack.Push(frame);
            return true;
        }

        if (c == '[')
        {
            top ??= new TopLevel { Kind = Kind.List };
            pos = SkipWhitespace(text, pos + 1);
            if (pos < text.Length && text[pos] == ']')
            {
                pos++;
                completed = Kind.List;
                return true;
            }

            stack.Push(new Frame(isObject: false));
            return true;
        }

        if (!TryParseScalar(text, ref pos, out var kind))
        {
            return false;
        }

        top ??= new TopLevel { Kind = kind };
        completed = kind;
        return true;
    }

    // Attaches a completed value to its parent, closing parents whose delimiter follows, and positions pos at the
    // next value after a comma (parsing the key first inside an object).
    private static Outcome CompleteValue(string text, ref int pos, Stack<Frame> stack, TopLevel top, Kind completed)
    {
        while (true)
        {
            if (stack.Count == 0)
            {
                pos = SkipWhitespace(text, pos);
                return pos == text.Length ? Outcome.Finished : Outcome.Failed;
            }

            var parent = stack.Peek();
            if (stack.Count == 1)
            {
                top.Record(parent, completed);
            }

            pos = SkipWhitespace(text, pos);
            if (pos >= text.Length)
            {
                return Outcome.Failed;
            }

            var closer = parent.IsObject ? '}' : ']';
            if (text[pos] == closer)
            {
                pos++;
                stack.Pop();
                completed = parent.IsObject ? Kind.Dict : Kind.List;
                continue;
            }

            if (text[pos] != ',')
            {
                return Outcome.Failed;
            }

            pos = SkipWhitespace(text, pos + 1);
            if (parent.IsObject)
            {
                if (!TryParseKey(text, ref pos, out var key))
                {
                    return Outcome.Failed;
                }

                parent.PendingKey = key;
            }

            return Outcome.NextValue;
        }
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

    private static bool TryParseScalar(string text, ref int pos, out Kind kind)
    {
        kind = Kind.None;
        switch (text[pos])
        {
            case '"':
                kind = Kind.Str;
                return TryScanString(text, pos + 1, out pos, builder: null);
            case 'n':
                kind = Kind.None;
                return TryLiteral(text, ref pos, "null");
            case 't':
                kind = Kind.Bool;
                return TryLiteral(text, ref pos, "true");
            case 'f':
                kind = Kind.Bool;
                return TryLiteral(text, ref pos, "false");
            case 'N':
                kind = Kind.Float;
                return TryLiteral(text, ref pos, "NaN");
            case 'I':
                kind = Kind.Float;
                return TryLiteral(text, ref pos, "Infinity");
            case '-' when TryLiteral(text, ref pos, "-Infinity"):
                kind = Kind.Float;
                return true;
            default:
                return TryMatchNumber(text, ref pos, out kind);
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
    private static bool TryMatchNumber(string text, ref int pos, out Kind kind)
    {
        kind = Kind.Int;
        var idx = pos;
        if (idx < text.Length && text[idx] == '-')
        {
            idx++;
            if (idx >= text.Length)
            {
                return false;
            }
        }

        if (idx < text.Length && text[idx] is >= '1' and <= '9')
        {
            idx++;
            while (idx < text.Length && IsAsciiDigit(text[idx]))
            {
                idx++;
            }
        }
        else if (idx < text.Length && text[idx] == '0')
        {
            idx++;
        }
        else
        {
            return false;
        }

        if (idx < text.Length && text[idx] == '.' && idx + 1 < text.Length && IsAsciiDigit(text[idx + 1]))
        {
            kind = Kind.Float;
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
                kind = Kind.Float;
                idx = exponentEnd;
            }
        }

        pos = idx;
        return true;
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
    private static bool TryScanString(string text, int start, out int end, StringBuilder? builder)
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
                builder?.Append(c);
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
    private static bool TryScanEscape(string text, int i, out int next, StringBuilder? builder)
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

            builder?.Append(mapped);
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
                builder?.Append(code).Append(code2);
                next = hexEnd + 6;
                return true;
            }
        }

        builder?.Append(code);
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
