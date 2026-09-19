using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Json;

/// <summary>JSON arrays built from .NET sequences.</summary>
internal static class JsonNodes
{
    /// <summary>The strings as a JSON array, in order.</summary>
    public static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode?)item)]);

    /// <summary>The numbers as a JSON array, in order.</summary>
    public static JsonArray Numbers(IEnumerable<int> items) => new([.. items.Select(item => (JsonNode?)item)]);

    /// <summary>The nodes as a JSON array, in order.</summary>
    public static JsonArray Array(IEnumerable<JsonNode?> items) => new([.. items]);
}
