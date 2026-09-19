using System.Text.Json;
using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Json;

/// <summary>Typed reads of a <see cref="JsonObject"/> member: each gives null (or false) when the member is absent or of another kind.</summary>
public static class JsonObjectReading
{
    extension(JsonObject json)
    {
        /// <summary>The member names in order.</summary>
        public IReadOnlyList<string> Keys => [.. json.Select(pair => pair.Key)];

        /// <summary>The member's string, or null.</summary>
        public string? Text(string key) => json[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

        /// <summary>The member's number when it is a whole number that fits an <see cref="int"/>, or null.</summary>
        public int? Int(string key) => json[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

        /// <summary>True only when the member is the literal <c>true</c>.</summary>
        public bool Flag(string key) => json[key]?.GetValueKind() == JsonValueKind.True;

        public JsonObject? Object(string key) => json[key] as JsonObject;

        public JsonArray? Array(string key) => json[key] as JsonArray;

        /// <summary>The strings of an array member, skipping anything else; empty when the member is not an array.</summary>
        public IReadOnlyList<string> Strings(string key) => json[key] is JsonArray items
            ? [.. items.OfType<JsonValue>().Where(item => item.GetValueKind() == JsonValueKind.String).Select(item => item.GetValue<string>())]
            : [];

        /// <summary>
        /// True when the member holds something: not absent, null, false, zero, an empty string, an empty array or an empty object.
        /// </summary>
        public bool HasContent(string key) => JsonObjectReading.HasContent(json[key]);
    }

    /// <summary>See <see cref="HasContent(JsonObject, string)"/>.</summary>
    public static bool HasContent(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        _ => node.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => node.GetValue<string>().Length > 0,
            JsonValueKind.Number => node.GetValue<double>() != 0,
            _ => true,
        },
    };
}
