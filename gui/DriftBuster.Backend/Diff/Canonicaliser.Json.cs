using System.Globalization;
using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

public static partial class Canonicaliser
{
    private const string JsonIndent = "  ";

    /// <summary>A container being written: its entries (dictionary items already sorted) and the next one to write.</summary>
    private sealed class JsonFrame(IReadOnlyList<KeyValuePair<string, object?>>? items, List<object?>? list, int depth)
    {
        public IReadOnlyList<KeyValuePair<string, object?>>? Items { get; } = items;

        public List<object?>? List { get; } = list;

        public int Depth { get; } = depth;

        public int Next { get; set; }

        public int Count => Items?.Count ?? List!.Count;
    }

    /// <summary>
    /// <c>canonicalise_json</c>: empty or all-white-space input gives an empty string; text <c>json.loads</c> accepts
    /// (after <c>str.strip</c>) is re-serialised as <c>json.dumps(parsed, ensure_ascii=False, sort_keys=True, indent=2)</c>;
    /// anything else goes through <see cref="CanonicaliseText"/>.
    /// </summary>
    /// <remarks>
    /// <c>json.loads</c> also raises <c>ValueError</c> past 4300 integer digits and <c>RecursionError</c> past the
    /// nesting limit; neither is a <c>JSONDecodeError</c>, so Python's diff aborts there. <see cref="PythonJson"/> refuses
    /// both, and the port falls back to text like any other undecodable payload.
    /// </remarks>
    public static string CanonicaliseJson(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        var stripped = PythonText.Strip(payload);
        if (stripped.Length == 0)
        {
            return string.Empty;
        }

        return PythonJson.TryLoads(stripped, out var parsed) ? DumpsSorted(parsed) : CanonicaliseText(payload);
    }

    /// <summary>
    /// The C encoder's output for <c>json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2)</c>: ", " never
    /// appears (items end with "," and a new line), keys follow ": ", dictionary items are ordered by key code point,
    /// empty containers are "{}" and "[]", floats use <c>float.__repr__</c> with NaN, Infinity and -Infinity spelled
    /// as JavaScript does. Nesting is walked on an explicit stack. With <paramref name="indent"/> false the output is
    /// <c>json.dumps(value, ensure_ascii=False, sort_keys=True)</c> instead: one line, items separated by ", ". With
    /// <paramref name="ensureAscii"/> the strings are escaped as <c>ensure_ascii=True</c> escapes them (the default of
    /// <c>json.dumps</c>): every UTF-16 unit outside space to "~" that has no short escape becomes <c>\uXXXX</c> in lower-case hex.
    /// </summary>
    internal static string DumpsSorted(object? value, bool indent = true, bool ensureAscii = false)
        => Dumps(value, indent, ensureAscii, sortKeys: true);

    /// <summary>
    /// <see cref="DumpsSorted"/> with <paramref name="sortKeys"/> false: <c>json.dumps(value, ensure_ascii=..., indent=...)</c>, dictionary
    /// items in insertion order. A container nested <paramref name="maxIndentDepth"/> levels below the document or deeper is written on one
    /// line, as without <paramref name="indent"/> (the indented layout grows with the square of the nesting depth).
    /// </summary>
    internal static string Dumps(object? value, bool indent, bool ensureAscii, bool sortKeys, int maxIndentDepth = int.MaxValue)
    {
        var builder = new StringBuilder();
        var stack = new Stack<JsonFrame>();
        WriteJsonValue(builder, stack, value, 0, ensureAscii, sortKeys);
        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            var indented = indent && frame.Depth < maxIndentDepth;
            if (frame.Next == frame.Count)
            {
                stack.Pop();
                if (indented)
                {
                    AppendNewLine(builder, frame.Depth);
                }

                builder.Append(frame.Items is null ? ']' : '}');
                continue;
            }

            if (frame.Next > 0)
            {
                builder.Append(indented ? "," : ", ");
            }

            if (indented)
            {
                AppendNewLine(builder, frame.Depth + 1);
            }

            object? item;
            if (frame.Items is { } items)
            {
                AppendJsonString(builder, items[frame.Next].Key, ensureAscii).Append(": ");
                item = items[frame.Next].Value;
            }
            else
            {
                item = frame.List![frame.Next];
            }

            frame.Next++;
            WriteJsonValue(builder, stack, item, frame.Depth + 1, ensureAscii, sortKeys);
        }

        return builder.ToString();
    }

    private static StringBuilder AppendNewLine(StringBuilder builder, int depth)
    {
        builder.Append('\n');
        for (var level = 0; level < depth; level++)
        {
            builder.Append(JsonIndent);
        }

        return builder;
    }

    private static void WriteJsonValue(StringBuilder builder, Stack<JsonFrame> stack, object? value, int depth, bool ensureAscii, bool sortKeys)
    {
        switch (value)
        {
            case OrderedDictionary<string, object?> dict when dict.Count == 0:
                builder.Append("{}");
                return;
            case OrderedDictionary<string, object?> dict:
                var items = dict.ToList();
                if (sortKeys)
                {
                    items.Sort((left, right) => PathText.CompareCodePoints(left.Key, right.Key));
                }

                builder.Append('{');
                stack.Push(new JsonFrame(items, null, depth));
                return;
            case List<object?> list when list.Count == 0:
                builder.Append("[]");
                return;
            case List<object?> list:
                builder.Append('[');
                stack.Push(new JsonFrame(null, list, depth));
                return;
            default:
                builder.Append(JsonScalar(value, ensureAscii));
                return;
        }
    }

    private static string JsonScalar(object? value, bool ensureAscii) => value switch
    {
        null => "null",
        true => "true",
        false => "false",
        string text => AppendJsonString(new StringBuilder(text.Length + 2), text, ensureAscii).ToString(),
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        BigInteger number => number.ToString(CultureInfo.InvariantCulture),
        double number when double.IsNaN(number) => "NaN",
        double number when double.IsPositiveInfinity(number) => "Infinity",
        double number when double.IsNegativeInfinity(number) => "-Infinity",
        double number => PythonRepr.Float(number),
        byte[] => throw new PythonTypeException("Object of type bytes is not JSON serializable", nameof(value)),
        _ => throw new ArgumentException($"Unsupported JSON value type {value.GetType()}", nameof(value)),
    };

    // escape_unicode (ensure_ascii=False): quote and backslash escaped, \b \f \n \r \t by name, other C0 controls as
    // \u00XX in lower-case hex; everything else, DEL, U+2028 and unpaired surrogates included, is written as is.
    // py_encode_basestring_ascii (ensure_ascii=True): the same short escapes, and every other unit outside " "-"~" (DEL, every
    // non-ASCII unit, each half of a surrogate pair) as \uXXXX in lower-case hex.
    private static StringBuilder AppendJsonString(StringBuilder builder, string text, bool ensureAscii)
    {
        builder.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                case > '~' when ensureAscii:
                    builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.Append('"');
    }
}
