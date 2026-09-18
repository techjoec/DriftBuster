using System.Text.Json;
using System.Text.Json.Serialization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Models
{
    /// <summary>
    /// Reads a run profile source from a bare string (the path) or an object, which is read exactly as a <c>profile.json</c> source is
    /// (<see cref="RunProfile.SourceFromDict"/>: a non-empty <c>path</c>, <c>str()</c> of a value that is not a string, an alias dropped
    /// when blank or falsy, <c>bool(optional)</c>, and <c>exclude</c> as one pattern for a string or the <c>str()</c> of each item); an
    /// object <see cref="RunProfile.SourceFromDict"/> refuses raises <see cref="JsonException"/> carrying its message. Writes a bare
    /// string when only the path is set, otherwise an object holding <c>path</c> and each of the other keys that is set.
    /// </summary>
    public sealed class RunProfileSourceJsonConverter : JsonConverter<RunProfileSource>
    {
        public override RunProfileSource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return new RunProfileSource(reader.GetString() ?? string.Empty);
                case JsonTokenType.StartObject:
                    break;
                default:
                    throw new JsonException($"A run profile source must be a string or an object, not {reader.TokenType}.");
            }

            using var document = JsonDocument.ParseValue(ref reader);
            if (!EngineJson.TryLoads(document.RootElement.GetRawText(), out var payload) || payload is not IReadOnlyDictionary<string, object?> mapping)
            {
                throw new JsonException("A run profile source object could not be read.");
            }

            try
            {
                return RunProfile.SourceFromDict(mapping);
            }
            catch (Exception exc) when (exc is ArgumentException or InvalidDataException or FormatException)
            {
                throw new JsonException(exc.Message, exc);
            }
        }

        public override void Write(Utf8JsonWriter writer, RunProfileSource value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);
            if (value.IsPathOnly)
            {
                writer.WriteStringValue(value.Path);
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("path", value.Path);
            if (value.Alias is not null)
            {
                writer.WriteString("alias", value.Alias);
            }

            if (value.Optional)
            {
                writer.WriteBoolean("optional", value: true);
            }

            if (value.Exclude is { Length: > 0 })
            {
                writer.WriteStartArray("exclude");
                foreach (var pattern in value.Exclude)
                {
                    writer.WriteStringValue(pattern);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
    }
}
