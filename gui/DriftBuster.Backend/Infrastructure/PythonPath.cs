namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>pathlib.Path</c> file-system queries with Python's semantics: <c>is_file()</c>, <c>realpath</c> and sorted <c>glob</c>.</summary>
public static class PythonPath
{
    /// <summary>
    /// <c>Path.is_file()</c>: true only when a <c>stat</c> that follows symlinks reports a regular file. A FIFO, a socket, a
    /// device, a directory, a dangling or looping link and a path that does not exist are not files, so no caller that
    /// walks with this check ever opens a pipe (which would block) or a device.
    /// </summary>
    /// <remarks>
    /// Linux asks the kernel for the file type (<see cref="UnixFileType"/>, <c>statx</c>; the file is never opened). As in Python,
    /// only a failure with <c>ENOENT</c>, <c>ENOTDIR</c>, <c>EBADF</c> or <c>ELOOP</c> means "not a file"; any other raises
    /// (<see cref="UnauthorizedAccessException"/> for a file inside a directory that cannot be searched, otherwise
    /// <see cref="IOException"/>). Windows
    /// has no FIFOs in a directory tree and reports devices through <see cref="FileAttributes.Device"/>: there, and on any
    /// Unix without <c>statx</c>, the path must exist, not be a directory or a device, and a link must resolve (its relative
    /// target taken against the physical directory holding the link, so <c>../shared/x.conf</c> reached through a directory
    /// link still resolves) to something that is neither.
    /// </remarks>
    public static bool IsFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (UnixFileType.Stat(path, followSymlinks: true) is { } kind)
        {
            return kind == UnixFileType.Kind.Regular;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return !IsDirectoryOrDevice(info.Attributes);
            }

            var physical = ResolvePhysicalPath(info.FullName);
            if (physical is null || !File.Exists(physical))
            {
                return false;
            }

            var attributes = new FileInfo(physical).Attributes;
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && !IsDirectoryOrDevice(attributes);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDirectoryOrDevice(FileAttributes attributes)
        => attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.Device);

    /// <summary>
    /// True for a directory entry whose name the runtime could not decode: on Linux a file name is bytes, .NET decodes
    /// the bytes of a listed name as UTF-8 with U+FFFD for each invalid sequence, and the decoded name then names nothing
    /// (Python keeps such a name through <c>surrogateescape</c> and opens it). The entry holds U+FFFD and a <c>lstat</c> of
    /// the decoded path fails with an error Python ignores; it is never opened. Always false where file names are not bytes, or
    /// without <c>statx</c>. Raises as <see cref="IsFile"/> raises.
    /// </summary>
    public static bool IsUndecodableName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathText.Name(path).Contains('\uFFFD', StringComparison.Ordinal)
            && UnixFileType.Stat(path, followSymlinks: false) == UnixFileType.Kind.Missing;
    }

    // Symlink hops the OS allows on one lookup before failing with ELOOP; Python's is_file() then returns False.
    private const int MaxLinkHops = 40;

    /// <summary>
    /// <c>realpath</c>: resolves every link component of <paramref name="fullPath"/> against the physical directory
    /// resolved so far (the OS semantics <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> does not follow, since it
    /// joins a relative target with the link's lexical directory). Null for a link loop or a target that cannot be read.
    /// </summary>
    public static string? ResolvePhysicalPath(string fullPath)
    {
        var hops = MaxLinkHops;
        return ResolvePhysicalPath(fullPath, ref hops);
    }

    private static string? ResolvePhysicalPath(string fullPath, ref int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var resolved = root.Length == 0 ? Path.DirectorySeparatorChar.ToString() : root;
        var components = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            if (string.Equals(component, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(component, "..", StringComparison.Ordinal))
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var candidate = Path.Join(resolved, component);
            var info = new FileInfo(candidate);
            string? target;
            try
            {
                target = info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? info.LinkTarget : null;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (hops-- <= 0)
            {
                return null;
            }

            var next = ResolvePhysicalPath(Path.IsPathRooted(target) ? target : Path.Join(resolved, target), ref hops);
            if (next is null)
            {
                return null;
            }

            resolved = next;
        }

        return resolved;
    }

    /// <summary>
    /// <c>sorted(Path(root).glob(pattern))</c> as path strings (<see cref="PythonGlob.Glob"/>): component by component, code
    /// point by code point, the order <c>PurePosixPath</c> sorts in (<see cref="PathText.ComparePosixPaths"/>). The same
    /// case-sensitive order is used on every platform.
    /// </summary>
    /// <remarks>
    /// Plan decision 4: a root directory that exists but cannot be listed raises its I/O error, where Python's glob swallows it
    /// and yields nothing. A missing root yields nothing, as in Python.
    /// </remarks>
    public static IReadOnlyList<string> SortedGlob(string root, string pattern, CancellationToken cancellationToken = default)
    {
        var paths = PythonGlob.Glob(root, pattern, cancellationToken).ToList();
        if (Directory.Exists(root))
        {
            using var listing = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            _ = listing.MoveNext();
        }

        paths.Sort((left, right) => PathText.ComparePosixPaths(PathText.ToPosix(left), PathText.ToPosix(right)));
        return paths;
    }
}
