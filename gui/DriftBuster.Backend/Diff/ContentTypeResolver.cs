using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// The canonicaliser a file is diffed with, chosen from what detection found in it rather than from the file extension:
/// <c>xml</c> when the catalog format is <c>structured-config-xml</c> or <c>xml</c>, <c>json</c> when it is <c>json</c>, otherwise
/// <c>text</c>. JSON canonicalises with sorted keys, so reordered keys are not drift; JSON that does not parse (comments,
/// trailing commas) falls back to text.
/// </summary>
public static class ContentTypeResolver
{
    public const string Text = "text";

    public const string Xml = "xml";

    public const string Json = "json";

    public static string FromCatalogFormat(string? catalogFormat) => catalogFormat switch
    {
        "structured-config-xml" or "xml" => Xml,
        "json" => Json,
        _ => Text,
    };

    /// <summary>The content type of a detection match: its <c>catalog_format</c> metadata, <c>text</c> when there is none.</summary>
    public static string FromMatch(DetectionMatch? match)
    {
        return match?.Metadata.Text("catalog_format") is { } format ? FromCatalogFormat(format) : Text;
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
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or MetadataValidationException)
        {
            return Text;
        }
    }

    /// <summary>
    /// The content type for diffing <paramref name="baselinePath"/> against <paramref name="candidatePath"/>: <c>xml</c>
    /// when either side detects as XML, else <c>json</c> when either side detects as JSON, otherwise <c>text</c>. A side
    /// that does not parse as the chosen type canonicalises as text.
    /// </summary>
    public static string ResolvePair(string baselinePath, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(baselinePath);
        ArgumentNullException.ThrowIfNull(candidatePath);
        var detector = NewDetector();
        string[] sides = [ResolveFile(baselinePath, detector), ResolveFile(candidatePath, detector)];
        return sides.Contains(Xml, StringComparer.Ordinal) ? Xml : sides.Contains(Json, StringComparer.Ordinal) ? Json : Text;
    }

    private static Detector NewDetector() => new(onWarning: static _ => { });
}
