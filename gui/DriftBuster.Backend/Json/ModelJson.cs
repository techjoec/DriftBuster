using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Json;

/// <summary>
/// JSON for DriftBuster's own files and command output: source-generated (<see cref="ModelJsonContext"/>), snake_case names,
/// string enums, indented UTF-8 with non-ASCII unescaped. Reads are strict: unknown or duplicate keys, a missing required member and
/// a null where the model does not allow one are refused with the JSON path.
/// </summary>
public static class ModelJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(ModelJsonContext.Default);

    /// <summary>The same contracts written on one line, for JSON lines records.</summary>
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(ModelJsonContext.Default, indented: false);

    /// <summary>The value as indented JSON with a trailing new line.</summary>
    public static string Serialize<T>(T value) => Serialize(value, TypeInfo<T>());

    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo) + "\n";

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>
    /// Read-only options over another source-generated context declared with the same <see cref="JsonSourceGenerationOptionsAttribute"/>
    /// settings as <see cref="ModelJsonContext"/>, with the same encoder; <paramref name="indented"/> false writes one line.
    /// </summary>
    public static JsonSerializerOptions CreateOptions(JsonSerializerContext context, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new JsonSerializerOptions(context.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = indented };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// One of DriftBuster's own files, read strictly. A file that does not parse, does not fit the model or holds null raises
    /// <see cref="InvalidDataException"/> naming the file and JSON path; a missing or unreadable file raises the I/O exception.
    /// </summary>
    public static T ReadFile<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? throw new InvalidDataException($"{path}: the file holds null.");
        }
        catch (JsonException exc)
        {
            throw new InvalidDataException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
    }

    /// <summary>Writes the value to <paramref name="path"/> through a temporary file, creating the directory.</summary>
    public static void WriteFile<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, Serialize(value, typeInfo));
    }
}
