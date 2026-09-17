namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Directory-tree globbing with the <see cref="PathWildcard"/> syntax. A pattern is split into segments on <c>/</c> (and <c>\</c> on
/// Windows). A segment without wildcards names an entry literally, a segment that is exactly <c>**</c> stands for zero or more
/// directory levels, and any other segment is matched against the names of the entries in a directory.
/// </summary>
/// <remarks>
/// Hidden and system entries are listed. A directory symbolic link or junction met while listing is returned when it matches
/// but never descended into. A directory that cannot be listed contributes nothing and a missing root yields nothing. The
/// cancellation token is checked before each directory is listed. Results are the root joined with the matched segments in
/// host separators, each at most once, in no particular order: callers that need an order sort them.
/// </remarks>
public static class FileTreeGlob
{
    private const string AnyDepth = "**";

    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = default,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The entries under <paramref name="root"/> that <paramref name="pattern"/> (relative to it) matches.</summary>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is empty or rooted.</exception>
    public static IReadOnlyList<string> Glob(string root, string pattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0)
        {
            throw new ArgumentException("A glob pattern must not be empty.", nameof(pattern));
        }

        if (Path.IsPathRooted(pattern) || pattern.StartsWith('/'))
        {
            throw new ArgumentException($"A glob pattern must be relative: {pattern}", nameof(pattern));
        }

        var segments = LexicalPath.Split(pattern).Segments;
        var results = new List<string>();
        if (segments.Count == 0 || !Directory.Exists(root))
        {
            return results;
        }

        Walk(root, segments, 0, new HashSet<string>(StringComparer.Ordinal), results, cancellationToken);
        return results;
    }

    /// <summary>
    /// The entries a pattern that carries its own base directory matches (<c>logs/**/*.log</c>, <c>C:\data\*.db</c>): the leading
    /// segments without wildcards (the root included) name the directory the rest is matched under, the working directory when
    /// there are none. A pattern without wildcards yields itself when it exists. Results are sorted by code point over their
    /// posix form.
    /// </summary>
    public static IReadOnlyList<string> GlobPathname(string pathname, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathname);
        var (root, segments) = LexicalPath.Split(pathname);
        var literal = segments.TakeWhile(segment => !PathWildcard.HasWildcards(segment)).Count();
        if (literal == segments.Count)
        {
            return pathname.Length > 0 && (File.Exists(pathname) || Directory.Exists(pathname) || IsLink(pathname)) ? [pathname] : [];
        }

        var separator = Path.DirectorySeparatorChar;
        var prefix = string.Join(separator, segments.Take(literal));
        var joinable = root.Length == 0 || prefix.Length == 0 || root[^1] == separator || root.EndsWith(':');
        var baseDirectory = joinable ? root + prefix : root + separator + prefix;

        var results = new List<string>();
        if (!Directory.Exists(baseDirectory.Length == 0 ? "." : baseDirectory))
        {
            return results;
        }

        Walk(baseDirectory, segments.Skip(literal).ToList(), 0, new HashSet<string>(StringComparer.Ordinal), results, cancellationToken);
        results.Sort((left, right) => PathText.CompareCodePoints(PathText.ToPosix(left), PathText.ToPosix(right)));
        return results;
    }

    // Matches segments[index..] below directory (spelled as results spell it; empty for the working directory).
    private static void Walk(
        string directory,
        IReadOnlyList<string> segments,
        int index,
        HashSet<string> seen,
        List<string> results,
        CancellationToken cancellationToken)
    {
        var segment = segments[index];
        var last = index == segments.Count - 1;
        if (string.Equals(segment, AnyDepth, StringComparison.Ordinal))
        {
            WalkAnyDepth(directory, segments, index, seen, results, cancellationToken);
            return;
        }

        if (!PathWildcard.HasWildcards(segment))
        {
            var child = Child(directory, segment);
            if (last)
            {
                if (File.Exists(child) || Directory.Exists(child) || IsLink(child))
                {
                    Add(child, seen, results);
                }
            }
            else if (Directory.Exists(child))
            {
                Walk(child, segments, index + 1, seen, results, cancellationToken);
            }

            return;
        }

        foreach (var entry in List(directory, cancellationToken))
        {
            if (!PathWildcard.IsMatch(entry.Name, segment))
            {
                continue;
            }

            var child = Child(directory, entry.Name);
            if (last)
            {
                Add(child, seen, results);
            }
            else if (IsWalkableDirectory(entry))
            {
                Walk(child, segments, index + 1, seen, results, cancellationToken);
            }
        }
    }

    // A ** segment: the rest of the pattern matched here (zero levels) and below every directory that is not a link.
    private static void WalkAnyDepth(
        string directory,
        IReadOnlyList<string> segments,
        int index,
        HashSet<string> seen,
        List<string> results,
        CancellationToken cancellationToken)
    {
        if (index < segments.Count - 1)
        {
            Walk(directory, segments, index + 1, seen, results, cancellationToken);
        }
        else if (directory.Length > 0)
        {
            Add(directory, seen, results);
        }

        foreach (var entry in List(directory, cancellationToken))
        {
            if (IsWalkableDirectory(entry))
            {
                Walk(Child(directory, entry.Name), segments, index, seen, results, cancellationToken);
            }
        }
    }

    // The entries of a directory, or none when it cannot be listed.
    private static List<FileSystemInfo> List(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new DirectoryInfo(directory.Length == 0 ? "." : directory).EnumerateFileSystemInfos("*", ListingOptions).ToList();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsWalkableDirectory(FileSystemInfo entry)
        => entry.Attributes.HasFlag(FileAttributes.Directory) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string Child(string directory, string name) => directory.Length == 0 ? name : Path.Join(directory, name);

    private static void Add(string path, HashSet<string> seen, List<string> results)
    {
        if (seen.Add(path))
        {
            results.Add(path);
        }
    }
}
