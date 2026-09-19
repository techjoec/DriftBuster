using System.Text.Json;
using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Tests;

/// <summary>Compares a JSON node with a plain value by its JSON: <c>1</c> and <c>1L</c>, a list and an array, a record and an object all agree.</summary>
internal static class JsonAssertions
{
    public static void ShouldBeJson(this JsonNode? actual, object? expected)
    {
        var expectedNode = expected as JsonNode ?? JsonSerializer.SerializeToNode(expected, expected?.GetType() ?? typeof(object));
        JsonNode.DeepEquals(actual, expectedNode).Should().BeTrue($"{actual?.ToJsonString() ?? "null"} should be {expectedNode?.ToJsonString() ?? "null"}");
    }
}
