namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Lexical path text for the host platform: no file system access and no working directory. A path is read as a root (what
/// <see cref="Path.GetPathRoot(string)"/> reports) followed by segments. Empty and <c>.</c> segments are dropped, <c>..</c> is kept.
/// </summary>
/// <remarks>
/// On Windows both <c>/</c> and <c>\</c> separate segments and results are written with <c>\</c>; elsewhere only <c>/</c> separates.
/// </remarks>
public static class LexicalPath
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    /// <summary>
    /// The normal spelling of <paramref name="path"/>: the root kept as spelled (with host separators), repeated separators collapsed,
    /// <c>.</c> segments dropped, trailing separators dropped except in a bare root, and <c>.</c> for an empty result.
    /// </summary>
    public static string Str(string path)
    {
        var (root, segments) = Split(path);
        return Format(root, segments, Path.DirectorySeparatorChar);
    }

    /// <summary><see cref="Str"/> with <c>/</c> as the only separator on every host; a backslash is an ordinary character.</summary>
    public static string PosixStr(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = path.StartsWith('/') ? "/" : string.Empty;
        return Format(root, Segments(path, ['/']), '/');
    }

    /// <summary>The root of <see cref="Str"/> (when non-empty) followed by its segments.</summary>
    public static IReadOnlyList<string> Parts(string path)
    {
        var (root, segments) = Split(path);
        return root.Length == 0 ? segments : [root, .. segments];
    }

    /// <summary>The root of <see cref="Str"/>, or an empty string.</summary>
    public static string Anchor(string path) => Split(path).Root;

    /// <summary><see cref="Path.IsPathFullyQualified(string)"/>.</summary>
    public static bool IsAbsolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.IsPathFullyQualified(path);
    }

    /// <summary>The last segment of <see cref="Str"/>, or an empty string when there is none (<c>.</c>, <c>/</c>, a bare root).</summary>
    public static string Name(string path)
    {
        var segments = Split(path).Segments;
        return segments.Count > 0 ? segments[^1] : string.Empty;
    }

    /// <summary>
    /// <see cref="Str"/> without the last segment: the root (or <c>.</c>) for a single segment, and the path itself when there are no segments.
    /// </summary>
    public static string Parent(string path)
    {
        var (root, segments) = Split(path);
        return Format(root, segments.Count == 0 ? segments : segments.Take(segments.Count - 1).ToList(), Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// <see cref="Str"/> of <see cref="Path.Combine(string, string)"/>: a rooted <paramref name="relative"/> replaces the path. On Windows a
    /// path such as <c>\x</c> counts as rooted, so it keeps no part of <paramref name="path"/> (not even its drive).
    /// </summary>
    public static string Join(string path, string relative)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(relative);
        return Str(Path.Combine(path, relative));
    }

    /// <summary>
    /// The segments of <paramref name="path"/> after those of <paramref name="root"/>, joined with <c>/</c> (<c>.</c> when the two are
    /// the same path), or null unless <paramref name="root"/> is the path or an ancestor of it by whole segments. Both are compared in
    /// their <see cref="Str"/> form, ordinally (ignoring case on Windows); a relative path never lies under an absolute root or the reverse.
    /// </summary>
    public static string? RelativeTo(string path, string root)
    {
        var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var (pathRoot, pathSegments) = Split(path);
        var (baseRoot, baseSegments) = Split(root);
        if (!string.Equals(pathRoot, baseRoot, comparison) || baseSegments.Count > pathSegments.Count)
        {
            return null;
        }

        for (var index = 0; index < baseSegments.Count; index++)
        {
            if (!string.Equals(pathSegments[index], baseSegments[index], comparison))
            {
                return null;
            }
        }

        return pathSegments.Count == baseSegments.Count ? "." : string.Join('/', pathSegments.Skip(baseSegments.Count));
    }

    /// <summary>The root (host separators) and the segments of <paramref name="path"/>.</summary>
    internal static (string Root, IReadOnlyList<string> Segments) Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var rest = path[root.Length..];
        if (Windows)
        {
            root = root.Replace('/', '\\');
        }

        char[] separators = Windows ? ['\\', '/'] : ['/'];
        return (root, Segments(rest, separators));
    }

    private static List<string> Segments(string text, char[] separators)
        => text.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.Equals(segment, ".", StringComparison.Ordinal))
            .ToList();

    private static string Format(string root, IReadOnlyList<string> segments, char separator)
    {
        if (segments.Count == 0)
        {
            return root.Length == 0 ? "." : root;
        }

        // A UNC root (\\server\share) ends without a separator, a drive-relative root (C:) runs straight into its first segment.
        var between = root.Length == 0 || root[^1] == separator || root.EndsWith(':') ? string.Empty : separator.ToString();
        return root + between + string.Join(separator, segments);
    }
}
