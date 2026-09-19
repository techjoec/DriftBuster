namespace DriftBuster.Backend.Infrastructure;

/// <summary>The file-system questions the scanners ask before opening anything.</summary>
public static class FilePaths
{
    /// <summary>
    /// True only for a regular file after following links. FIFOs, sockets, devices, directories and dangling or looping links are
    /// false, so a walk never opens a pipe (which blocks) or a device. Linux asks <c>statx</c> (<see cref="UnixFileType"/>) without
    /// opening the file; elsewhere the file must exist and be neither a directory nor a device.
    /// </summary>
    public static bool IsFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (UnixFileType.Stat(path, followSymlinks: true) is { } kind)
        {
            return kind == UnixFileType.Kind.Regular;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            FileSystemInfo info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            }

            return info.Exists && !info.Attributes.HasFlag(FileAttributes.Directory) && !info.Attributes.HasFlag(FileAttributes.Device);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for an entry that is not a file under its decoded name while that name holds U+FFFD: on Linux, the name's bytes are not
    /// UTF-8 and .NET cannot spell it, so the entry exists but cannot be opened.
    /// </summary>
    public static bool IsUndecodableName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetFileName(path).Contains('\uFFFD', StringComparison.Ordinal) && !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>The full path, with the final target when the path is itself a link; the full path when the link cannot be read.</summary>
    public static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            return info.LinkTarget is null ? full : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>
    /// <see cref="FileTreeGlob.Glob"/> matches sorted by path segment (<see cref="PathText.ComparePosixPaths"/>). A root that exists
    /// but cannot be listed throws; a missing root yields nothing.
    /// </summary>
    public static IReadOnlyList<string> SortedGlob(string root, string pattern, CancellationToken cancellationToken = default)
    {
        var paths = FileTreeGlob.Glob(root, pattern, cancellationToken).ToList();
        if (Directory.Exists(root))
        {
            using var listing = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            _ = listing.MoveNext();
        }

        paths.Sort((left, right) => PathText.ComparePosixPaths(PathText.ToPosix(left), PathText.ToPosix(right)));
        return paths;
    }
}
