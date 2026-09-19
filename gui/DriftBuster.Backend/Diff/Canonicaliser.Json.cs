using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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

    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = ScannedJson.MaxDepth,
    };

    /// <summary>
    /// Empty or whitespace-only input gives ""; plain JSON (<see cref="ScannedJson.Strict"/>) is re-written indented by 2 with object
    /// keys in ordinal order (duplicates kept, in order), numbers as written and non-ASCII unescaped. Anything else, including JSON
    /// with comments, goes through <see cref="CanonicaliseText"/> so a comment change still shows in the diff.
    /// </summary>
    public static string CanonicaliseJson(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        using var document = ScannedJson.TryParse(payload.Trim(), ScannedJson.Strict);
        if (document is null)
        {
            return CanonicaliseText(payload);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanonicalWriterOptions))
        {
            WriteSorted(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Recursion is bounded by ScannedJson.MaxDepth.
    private static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSorted(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// JSON with sorted keys (code-point order): indented output uses "," line ends and ": " after keys; empty containers are "{}"
    /// and "[]"; floats in shortest round-trip form with NaN/Infinity/-Infinity. Without <paramref name="indent"/>, one line with ", "
    /// separators. With <paramref name="ensureAscii"/>, every UTF-16 unit outside space..~ without a short escape becomes lower-case
    /// <c>\uXXXX</c>. Explicit stack.
    /// </summary>
    internal static string DumpsSorted(object? value, bool indent = true, bool ensureAscii = false)
        => Dumps(value, indent, ensureAscii, sortKeys: true);

    /// <summary>
    /// <see cref="DumpsSorted"/> with optional key sorting (insertion order otherwise). Containers <paramref name="maxIndentDepth"/> levels
    /// deep or deeper are written on one line, since the indented layout grows with the square of the depth.
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
        double number => EngineRepr.Float(number),
        byte[] => throw new NotSupportedException("A value of type 'byte array' cannot be written as JSON."),
        _ => throw new ArgumentException($"Unsupported JSON value type {value.GetType()}", nameof(value)),
    };

    // Quote and backslash escaped, \b \f \n \r \t by name, other C0 controls as lower-case \u00XX; with ensureAscii also every other
    // unit outside space..~ (DEL, non-ASCII, each surrogate half). Without it, DEL, U+2028 and unpaired surrogates pass through.
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
