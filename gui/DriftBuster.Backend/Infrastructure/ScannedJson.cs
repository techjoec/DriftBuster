using System.Text.Json;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// JSON reads for scanned files (the configs being compared). Lenient where real configs are: comments and trailing commas are
/// accepted and duplicate keys are kept in order. <see cref="Strict"/> accepts plain RFC 8259 only, for callers where a comment
/// must not disappear silently (the diff canonicaliser).
/// </summary>
internal static class ScannedJson
{
    /// <summary>Deepest nesting read; deeper documents are treated as not JSON.</summary>
    public const int MaxDepth = 256;

    public static JsonDocumentOptions Lenient { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = MaxDepth,
    };

    public static JsonDocumentOptions Strict { get; } = new() { MaxDepth = MaxDepth };

    /// <summary>The parsed document, or null when the text is not one JSON value under <paramref name="options"/>. A leading U+FEFF is ignored.</summary>
    public static JsonDocument? TryParse(string text, JsonDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return JsonDocument.Parse(text.StartsWith('﻿') ? text.AsMemory(1) : text.AsMemory(), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A scalar's display text: a string as itself, anything else as its JSON text as written.</summary>
    public static string ScalarText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
}
