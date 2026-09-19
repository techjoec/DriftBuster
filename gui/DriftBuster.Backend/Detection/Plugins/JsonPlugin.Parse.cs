using System.Text.Json;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Best-effort parse of the comment-free sample producing top-level metadata.</summary>
public sealed partial class JsonPlugin
{
    /// <summary>Outcome of <see cref="AttemptParse"/>: whether the snippet parsed and the metadata it contributes.</summary>
    internal sealed record ParseResult(bool Success, OrderedDictionary<string, object?> Metadata)
    {
        internal static ParseResult Failure { get; } = new(false, new OrderedDictionary<string, object?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Parses the structurally complete prefix of <paramref name="text"/> as plain JSON and reports the top-level type and the first
    /// five distinct object keys or the types of the first five array items. With <paramref name="allowComments"/> the parse is skipped.
    /// </summary>
    internal static ParseResult AttemptParse(string text, bool allowComments)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (allowComments)
        {
            return ParseResult.Failure;
        }

        var snippet = TruncateToStructuralBoundary(text);
        using var document = snippet.Length == 0 ? null : ScannedJson.TryParse(snippet, ScannedJson.Strict);
        if (document is null)
        {
            return ParseResult.Failure;
        }

        var root = document.RootElement;
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object)
        {
            metadata["top_level_type"] = "object";
            metadata["top_level_keys"] = root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Take(TopLevelKeyLimit).ToList();
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            metadata["top_level_type"] = "array";
            var names = root.EnumerateArray().Take(TopLevelKeyLimit).Select(TypeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (names.Count > 0)
            {
                metadata["top_level_sample_types"] = names;
            }
        }

        return new ParseResult(true, metadata);
    }

    private static string TypeName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0 ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };
}
