using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DriftBuster.Backend.Json;

/// <summary>
/// JSON for DriftBuster's own files and command output: source-generated (<see cref="ModelJsonContext"/>), snake_case names,
/// string enums, indented UTF-8 with non-ASCII unescaped. Reads are strict: unknown or duplicate keys, a missing required member and
/// a null where the model does not allow one are refused with the JSON path.
/// </summary>
public static class ModelJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>The value as indented JSON with a trailing new line.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>()) + "\n";

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(ModelJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.MakeReadOnly();
        return options;
    }
}
