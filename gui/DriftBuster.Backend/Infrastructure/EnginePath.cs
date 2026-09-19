namespace DriftBuster.Backend.Infrastructure;

/// <summary>File-system queries that follow the kernel's path semantics (links, <c>..</c>, non-UTF-8 names) rather than the runtime's lexical ones.</summary>
public static class EnginePath
{
    /// <summary>
    /// True only for a regular file after following links. FIFOs, sockets, devices, directories and dangling or looping links are
    /// false, so walkers never open a pipe (which blocks) or a device.
    /// </summary>
    /// <remarks>
    /// Linux uses <c>statx</c> without opening the file; only ENOENT, ENOTDIR, EBADF and ELOOP mean "not a file", other errors throw.
    /// Elsewhere the path must exist, not be a directory or device, and its links must resolve against the link's physical directory.
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
    /// True for a Linux directory entry whose byte name is not UTF-8: .NET decodes it with U+FFFD, so the decoded path names
    /// nothing and must not be opened. False where names are not bytes or without <c>statx</c>.
    /// </summary>
    public static bool IsUndecodableName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathText.Name(path).Contains('\uFFFD', StringComparison.Ordinal)
            && UnixFileType.Stat(path, followSymlinks: false) == UnixFileType.Kind.Missing;
    }

    // Link hops before the kernel fails a lookup with ELOOP.
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Joins a relative path onto the working directory without normalising, so <c>..</c> stays in place
    /// (<see cref="Path.GetFullPath(string)"/> would remove it). Windows <c>\x</c> and <c>C:x</c> join onto their root's full path.
    /// </summary>
    public static string Absolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (LexicalPath.IsAbsolute(path))
        {
            return LexicalPath.Str(path);
        }

        var (root, segments) = LexicalPath.Split(path);
        var directory = root.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
        return LexicalPath.Str(Path.Join(directory, string.Join(Path.DirectorySeparatorChar, segments)));
    }

    /// <summary>Expands a leading <c>~</c> or <c>~user</c> segment; any other path is returned as is.</summary>
    /// <exception cref="InvalidOperationException">No home directory for the user.</exception>
    /// <exception cref="ArgumentException">A <c>~user</c> name holding NUL.</exception>
    public static string ExpandUser(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var (root, segments) = LexicalPath.Split(path);
        if (root.Length > 0 || segments.Count == 0 || !segments[0].StartsWith('~'))
        {
            return LexicalPath.Str(path);
        }

        var home = EngineOsPath.ExpandUser(segments[0]);
        if (home.StartsWith('~'))
        {
            throw new InvalidOperationException("Could not determine home directory.");
        }

        return LexicalPath.Str(Path.Join(home, string.Join(Path.DirectorySeparatorChar, segments.Skip(1))));
    }

    /// <summary>
    /// The absolute path with links resolved. Windows replaces the deepest existing link prefix with its final target; elsewhere
    /// every link and <c>..</c> is followed physically. Missing components are kept as written; a loop or unreadable link falls
    /// back to the lexically normalised path.
    /// </summary>
    /// <exception cref="ArgumentException">On Unix, a path holding NUL.</exception>
    public static string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // A NUL anywhere in the path is refused before any lookup.
        if (!OperatingSystem.IsWindows())
        {
            FileSystemError.ThrowIfEmbeddedNull(path);
        }

        if (OperatingSystem.IsWindows())
        {
            return ResolveWindows(path);
        }

        var absolute = Absolute(path);
        var physical = ResolvePhysicalPath(absolute, out var nameable);
        return physical is null ? Path.GetFullPath(absolute) : nameable ? physical : Path.GetFullPath(KernelPath(absolute));
    }

    private static string ResolveWindows(string path)
    {
        var full = Path.GetFullPath(path.Length == 0 ? "." : path);
        var probe = full;
        var rest = string.Empty;
        while (probe is not null)
        {
            if (File.Exists(probe) || Directory.Exists(probe))
            {
                try
                {
                    FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);
                    if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    {
                        probe = target.FullName;
                    }
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    // An unreadable link keeps the path as written.
                }

                return LexicalPath.Str(Path.Join(probe, rest));
            }

            rest = Path.Join(Path.GetFileName(probe), rest);
            probe = Path.GetDirectoryName(probe);
        }

        return LexicalPath.Str(full);
    }

    /// <summary>
    /// A spelling of <paramref name="path"/> that the runtime's file APIs resolve to the same entry the kernel does. The runtime
    /// removes <c>..</c> lexically (<c>link/../x</c> becomes <c>x</c>) while the kernel steps to the parent of the link's target,
    /// so every part up to the last <c>..</c> is walked physically. A directory reachable only by a non-UTF-8 name yields a path
    /// under <see cref="Unreachable"/>. Paths without <c>..</c>, and all Windows paths, are returned unchanged.
    /// </summary>
    public static string KernelPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows() || !path.Contains("..", StringComparison.Ordinal) || !path.Split('/').Contains("..", StringComparer.Ordinal))
        {
            return path;
        }

        var components = Absolute(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = Array.LastIndexOf(components, "..");
        if (UnixPathWalk.KernelPrefix(components[..(last + 1)]) is { } walked)
        {
            if (!walked.Nameable)
            {
                return Beneath(Unreachable, components.Skip(walked.Outcome == UnixPathWalk.Outcome.Failed ? walked.FailedIndex : last + 1));
            }

            return walked.Outcome == UnixPathWalk.Outcome.Failed
                ? UnderFailedPart(walked.Text, components, walked.FailedIndex)
                : Path.Join(walked.Text, string.Join('/', components.Skip(last + 1)));
        }

        var resolved = "/";
        for (var index = 0; index <= last; index++)
        {
            var component = components[index];
            if (string.Equals(component, "..", StringComparison.Ordinal))
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var physical = ResolvePhysicalPath(Path.Join(resolved, component));
            if (physical is null || !IsDirectoryAfterLinks(physical))
            {
                return UnderFailedPart(resolved, components, index);
            }

            resolved = physical;
        }

        return Path.Join(resolved, string.Join('/', components.Skip(last + 1)));
    }

    /// <summary>
    /// Creates every missing directory, applying a <c>..</c> after a not-yet-existing directory once it exists
    /// (<c>new/../sub</c> creates <c>new</c> and <c>sub</c>). Existing directories are left alone.
    /// </summary>
    /// <remarks>
    /// On Linux, failures throw <see cref="FileSystemError.Create"/>'s exception for the errno, naming the directory whose
    /// <c>mkdir</c> failed; elsewhere the runtime's own exceptions apply.
    /// </remarks>
    /// <exception cref="ArgumentException">The path holds NUL.</exception>
    public static void MakeDirectories(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileSystemError.ThrowIfEmbeddedNull(path);
        var spelled = LexicalPath.Str(path);
        if (!UnixPathWalk.Disabled && UnixMkdir.MakeDirectory(spelled) is { } error)
        {
            MakeDirectoryChecked(spelled, error, parents: true);
            return;
        }

        var kernel = KernelPath(path);
        if (Directory.Exists(kernel))
        {
            return;
        }

        var parent = LexicalPath.Parent(path);
        if (!string.Equals(kernel, path, StringComparison.Ordinal) && !string.Equals(parent, path, StringComparison.Ordinal))
        {
            MakeDirectories(parent);
            kernel = KernelPath(path);
        }

        Directory.CreateDirectory(kernel);
    }

    // After a failed mkdir (error 0 = created): ENOENT with parents creates the parent and retries once; any other error is
    // ignored only when the path is already a directory.
    private static void MakeDirectoryChecked(string path, int error, bool parents)
    {
        if (error == 0)
        {
            return;
        }

        if (error == FileSystemError.NoSuchFile)
        {
            var parent = LexicalPath.Parent(path);
            if (!parents || string.Equals(parent, path, StringComparison.Ordinal))
            {
                throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
            }

            MakeDirectoryChecked(parent, MakeDirectoryNative(parent), parents: true);
            MakeDirectoryChecked(path, MakeDirectoryNative(path), parents: false);
            return;
        }

        if (UnixFileType.Stat(path, followSymlinks: true) != UnixFileType.Kind.Directory)
        {
            throw FileSystemError.Create(error, path);
        }
    }

    private static int MakeDirectoryNative(string path)
        => UnixMkdir.MakeDirectory(path) ?? throw new InvalidOperationException("mkdir(2) became unavailable during a call that used it.");

    /// <summary>
    /// Where <see cref="KernelPath"/> puts a path that has no UTF-8 spelling: under a character device, so nothing there
    /// exists, opens or can be created.
    /// </summary>
    internal const string Unreachable = "/dev/null/unreachable";

    private static string UnderFailedPart(string reached, string[] components, int failedIndex)
        => string.Equals(components[failedIndex], "..", StringComparison.Ordinal)
            ? Beneath(Unreachable, components.Skip(failedIndex))
            : Beneath(Path.Join(reached, components[failedIndex]), components.Skip(failedIndex + 1));

    // Parts under a directory that does not exist, each ".." spelled "..." so the runtime's lexical removal cannot climb out.
    private static string Beneath(string directory, IEnumerable<string> parts)
        => Path.Join(directory, string.Join('/', parts.Select(part => string.Equals(part, "..", StringComparison.Ordinal) ? "..." : part)));

    private static bool IsDirectoryAfterLinks(string physical)
    {
        try
        {
            return UnixFileType.Stat(physical, followSymlinks: true) is { } kind ? kind == UnixFileType.Kind.Directory : Directory.Exists(physical);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves every link against the physical directory reached so far (unlike <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/>,
    /// which uses the link's lexical directory). Null for a loop or unreadable link.
    /// </summary>
    public static string? ResolvePhysicalPath(string fullPath) => ResolvePhysicalPath(fullPath, out _);

    /// <summary>
    /// <see cref="ResolvePhysicalPath(string)"/>; <paramref name="nameable"/> is false when the result holds a non-UTF-8 name, so
    /// it must not be opened.
    /// </summary>
    internal static string? ResolvePhysicalPath(string fullPath, out bool nameable)
    {
        nameable = true;
        if (UnixPathWalk.TryResolvePhysical(fullPath, out var walked))
        {
            nameable = walked is not { Nameable: false };
            return walked?.Text;
        }

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
    /// <see cref="FileTreeGlob.Glob"/> matches sorted case-sensitively by posix path segment on every platform. A root that
    /// exists but cannot be listed throws; a missing root yields nothing.
    /// </summary>
    public static IReadOnlyList<string> SortedGlob(string root, string pattern, CancellationToken cancellationToken = default)
    {
        var paths = FileTreeGlob.Glob(root, pattern, cancellationToken).ToList();
        var kernelRoot = KernelPath(root);
        if (Directory.Exists(kernelRoot))
        {
            using var listing = Directory.EnumerateFileSystemEntries(kernelRoot).GetEnumerator();
            _ = listing.MoveNext();
        }

        paths.Sort((left, right) => PathText.ComparePosixPaths(PathText.ToPosix(left), PathText.ToPosix(right)));
        return paths;
    }
}
