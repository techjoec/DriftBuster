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
}
