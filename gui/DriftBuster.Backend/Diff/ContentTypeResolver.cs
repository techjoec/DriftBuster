using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// The canonicaliser a file is diffed with, chosen from what detection found in it (plan fix f: the diff planner used a
/// file-extension allowlist). The rule is <c>driftbuster.multi_server._determine_content_type</c>, the only Python
/// caller that chooses from detection: <c>xml</c> when the catalog format is <c>structured-config-xml</c> or <c>xml</c>,
/// otherwise <c>text</c>. JSON is diffed as text: <c>canonicalise_json</c> exists but no Python caller selects it, and
/// multi-server output (phase 5) must keep Python's canonical payloads.
/// </summary>
public static class ContentTypeResolver
{
    /// <summary>The content type for text that is not XML.</summary>
    public const string Text = "text";

    /// <summary>The content type for XML documents.</summary>
    public const string Xml = "xml";

    /// <summary><c>_determine_content_type(catalog_format)</c>.</summary>
    public static string FromCatalogFormat(string? catalogFormat)
        => catalogFormat is "structured-config-xml" or "xml" ? Xml : Text;

    /// <summary>The content type of a detection match: its <c>catalog_format</c> metadata, <c>text</c> when there is none.</summary>
    public static string FromMatch(DetectionMatch? match)
    {
        if (match?.Metadata is { } metadata && metadata.TryGetValue("catalog_format", out var format) && format is string text)
        {
            return FromCatalogFormat(text);
        }

        return Text;
    }

    /// <summary>
    /// Detects <paramref name="path"/> with the default registry and maps the match. A file detection cannot read, or whose
    /// match the catalog rejects, is text.
    /// </summary>
    public static string ResolveFile(string path, Detector? detector = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return FromMatch((detector ?? NewDetector()).ScanFile(path));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or MetadataValidationError)
        {
            return Text;
        }
    }

    /// <summary>
    /// The content type for diffing <paramref name="baselinePath"/> against <paramref name="candidatePath"/>: <c>xml</c>
    /// when either side detects as XML (a document whose other side is malformed still canonicalises as XML where it
    /// parses, and falls back to text where it does not), otherwise <c>text</c>.
    /// </summary>
    public static string ResolvePair(string baselinePath, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(baselinePath);
        ArgumentNullException.ThrowIfNull(candidatePath);
        var detector = NewDetector();
        return IsXml(ResolveFile(baselinePath, detector)) || IsXml(ResolveFile(candidatePath, detector)) ? Xml : Text;
    }

    private static bool IsXml(string contentType) => string.Equals(contentType, Xml, StringComparison.Ordinal);

    private static Detector NewDetector() => new(onWarning: static _ => { });
}
