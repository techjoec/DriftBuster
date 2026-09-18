namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Best-effort parse of the comment-free sample producing top-level metadata.</summary>
public sealed partial class JsonPlugin
{
    /// <summary>Outcome of <see cref="AttemptParse"/>: whether the snippet parsed and the metadata it contributes.</summary>
    internal sealed record ParseResult(bool Success, OrderedDictionary<string, object?> Metadata)
    {
        internal static ParseResult Failure { get; } = new(false, new OrderedDictionary<string, object?>(StringComparer.Ordinal));
    }

    private static readonly string[] TypeNames = ["object", "array", "string", "integer", "number", "boolean", "null"];

    /// <summary>
    /// Parses the structurally complete prefix of <paramref name="text"/> with the acceptance rules of Python's
    /// <c>json.loads</c> and reports the top-level type, the first five object keys, or the JSON type names of the
    /// first five array items. Comments are never accepted: with <paramref name="allowComments"/> the parse is skipped.
    /// </summary>
    internal static ParseResult AttemptParse(string text, bool allowComments)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (allowComments)
        {
            return ParseResult.Failure;
        }

        var snippet = TruncateToStructuralBoundary(text);
        if (snippet.Length == 0)
        {
            return ParseResult.Failure;
        }

        var parsed = EngineJsonScanner.Parse(snippet);
        if (parsed is null)
        {
            return ParseResult.Failure;
        }

        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (parsed.Kind == EngineJsonScanner.Kind.Dict)
        {
            metadata["top_level_type"] = "object";
            metadata["top_level_keys"] = parsed.Keys.Take(TopLevelKeyLimit).ToList();
        }
        else if (parsed.Kind == EngineJsonScanner.Kind.List)
        {
            metadata["top_level_type"] = "array";
            if (parsed.ItemKinds.Count > 0)
            {
                // sorted({type(item).__name__ ...}): the names are ASCII, so ordinal order is Python's order.
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var kind in parsed.ItemKinds.Take(TopLevelKeyLimit))
                {
                    names.Add(TypeNames[(int)kind]);
                }

                metadata["top_level_sample_types"] = names.ToList();
            }
        }

        return new ParseResult(true, metadata);
    }
}
