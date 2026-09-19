using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// Text, JSON and XML canonicalisation: payloads normalised before diffing so formatting noise does not show up as drift.
/// </summary>
public static partial class Canonicaliser
{
    private const char Bom = '\uFEFF';

    /// <summary>The content types with a normaliser.</summary>
    public static IReadOnlyList<string> ContentTypes { get; } = ["text", "json", "xml"];

    /// <summary>
    /// The version of the canonical forms. Bump it whenever a canonicaliser's output changes, so caches keyed on it
    /// (the multi-server diff cache) stop serving forms the current build no longer produces.
    /// </summary>
    public const int FormVersion = 2;

    /// <summary>True when <paramref name="contentType"/> names a normaliser (exact, case-sensitive).</summary>
    public static bool IsSupported(string contentType) => ContentTypes.Contains(contentType, StringComparer.Ordinal);

    /// <summary>
    /// The normaliser for <paramref name="contentType"/> applied to <paramref name="payload"/>; an unknown type raises
    /// <see cref="ArgumentException"/> (<c>Unsupported content_type: ...</c>).
    /// </summary>
    public static string Canonicalise(string payload, string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        return contentType switch
        {
            "text" => CanonicaliseText(payload),
            "json" => CanonicaliseJson(payload),
            "xml" => CanonicaliseXml(payload),
            _ => throw UnsupportedContentType(contentType),
        };
    }

    internal static ArgumentException UnsupportedContentType(string contentType) => new($"Unsupported content_type: {contentType}", nameof(contentType));

    /// <summary>
    /// Leading U+FEFF removed; U+2028, U+2029 and U+0085 become LF; CRLF then CR become LF; trailing whitespace
    /// (<see cref="char.IsWhiteSpace(char)"/>) trimmed per line; lines joined with LF (a trailing break survives as a final empty line).
    /// </summary>
    public static string CanonicaliseText(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        var working = payload.TrimStart(Bom);
        working = working.Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n');
        var normalised = working.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalised.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = lines[index].TrimEnd();
        }

        return string.Join("\n", lines);
    }
}
