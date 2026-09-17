namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>pathlib.Path</c> file-system queries with Python's semantics: <c>is_file()</c>, <c>realpath</c> and sorted <c>glob</c>.</summary>
public static class EnginePath
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
    /// A relative path joined onto the working directory in <see cref="LexicalPath.Str"/> form, with no other normalisation, so a
    /// <c>..</c> segment stays where it is (<see cref="Path.GetFullPath(string)"/> would remove it). A fully qualified path is returned in
    /// <see cref="LexicalPath.Str"/> form. On Windows a path with a root but no drive (<c>\x</c>) or a drive but no root (<c>C:x</c>) is
    /// joined onto the directory <see cref="Path.GetFullPath(string)"/> gives for that root.
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

    /// <summary>
    /// A path with no root whose first segment starts with <c>~</c> has that segment replaced by <see cref="EngineOsPath.ExpandUser(string)"/>
    /// of it; any other path is returned in <see cref="LexicalPath.Str"/> form.
    /// </summary>
    /// <exception cref="EngineRuntimeException">The first segment is still <c>~</c>-prefixed after expansion (an account the password
    /// database does not hold, or no home directory at all): <c>Could not determine home directory.</c></exception>
    /// <exception cref="EngineValueException">A posix <c>~user</c> name holding a NUL character (<c>embedded null byte</c>).</exception>
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
            throw new EngineRuntimeException("Could not determine home directory.");
        }

        return LexicalPath.Str(Path.Join(home, string.Join(Path.DirectorySeparatorChar, segments.Skip(1))));
    }

    /// <summary>
    /// The absolute path with links resolved. On Windows the path is made full (<see cref="Path.GetFullPath(string)"/>) and, when the
    /// entry or its deepest existing ancestor is a link, that prefix is replaced by the link's final target; segments that do not exist
    /// are kept as written (letter case and 8.3 names are left as given). Elsewhere the path is made absolute
    /// against the working directory, then every link and <c>..</c> followed physically (<see cref="ResolvePhysicalPath(string, out bool)"/>),
    /// a component that does not exist kept as written. A directory reached only through a name that is not UTF-8 keeps the kernel's
    /// spelling of it (<see cref="KernelPath"/>), which reaches the same entry through its links; a link loop or a link that cannot be read
    /// falls back to the lexically normalised absolute path.
    /// </summary>
    /// <exception cref="EngineValueException">On posix, a path holding a NUL character (<c>lstat: embedded null character in path</c>).</exception>
    public static string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // posixpath.realpath lstats every component, and the first lstat refuses a NUL anywhere in the path.
        if (!OperatingSystem.IsWindows())
        {
            OsError.ThrowIfEmbeddedNull(path, "lstat");
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
    /// A spelling of <paramref name="path"/> that the runtime's file APIs resolve to the entry the operating system resolves
    /// <paramref name="path"/> to. The runtime removes <c>..</c> parts lexically before every call (<c>link/../x</c> becomes
    /// <c>x</c>), where a POSIX kernel, and so Python, steps to the parent of wherever <c>link</c> leads. Every part up to the last
    /// <c>..</c> is therefore walked as the kernel walks it and the rest is appended as written, so a final link stays a link. On
    /// Linux the walk reads link targets as bytes (<see cref="UnixPathWalk"/>) and expands a link only when a <c>..</c> steps out
    /// of it, so a target whose name is not UTF-8 is never looked up under the U+FFFD spelling the runtime would give it. A part
    /// before a <c>..</c> that is missing, a loop or not a directory (the kernel fails that lookup) yields a path under it that
    /// cannot exist either. When the directory the kernel reaches can only be named with bytes that are not UTF-8, no spelling
    /// the runtime accepts reaches it: the result is <see cref="Unreachable"/> joined with the rest, which names nothing. A path
    /// without <c>..</c>, and every path on Windows (whose own path parsing removes <c>..</c> lexically, as the runtime does), is
    /// returned unchanged.
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
    /// <c>Path(path).mkdir(parents=True, exist_ok=True)</c>: every missing directory is created as the kernel reaches it. A <c>..</c>
    /// after a directory that does not exist yet steps out of that directory once it is created, as Python's retry on
    /// <c>FileNotFoundError</c> does (<c>new/../sub</c> creates <c>new</c> and <c>sub</c>), where <see cref="KernelPath"/> alone names a
    /// path under the missing part that nothing can create. A directory that already exists is left as it is.
    /// </summary>
    /// <remarks>
    /// On Linux this is <c>Path.mkdir</c>'s own algorithm over <c>mkdir(2)</c> (<see cref="UnixMkdir"/>): a failure raises the
    /// <see cref="IOException"/> <see cref="OsError"/> builds for the call's <c>errno</c> (its <see cref="Exception.HResult"/>)
    /// naming the directory whose call failed as <c>str(Path)</c> spells it (<c>[Errno 17] File exists: 'afile'</c>,
    /// <c>[Errno 20] Not a directory: 'afile/x'</c>). Elsewhere, and for a path holding an unpaired surrogate (or with the
    /// <see cref="UnixPathWalk.Disabled"/> seam set), the directories are created through the runtime, whose exceptions carry its own text.
    /// </remarks>
    /// <exception cref="EngineValueException">The path holds a NUL character (<c>mkdir: embedded null character in path</c>).</exception>
    public static void MakeDirectories(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        OsError.ThrowIfEmbeddedNull(path, "mkdir");
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

    // pathlib.Path.mkdir(exist_ok=True) after its first os.mkdir(path) failed with error (0: created). ENOENT with parents creates the
    // parent and retries once without parents; any other error is ignored only when is_dir() is True, whose own raise wins.
    private static void MakeDirectoryChecked(string path, int error, bool parents)
    {
        if (error == 0)
        {
            return;
        }

        if (error == OsError.NoSuchFile)
        {
            var parent = LexicalPath.Parent(path);
            if (!parents || string.Equals(parent, path, StringComparison.Ordinal))
            {
                throw OsError.Create(error, path);
            }

            MakeDirectoryChecked(parent, MakeDirectoryNative(parent), parents: true);
            MakeDirectoryChecked(path, MakeDirectoryNative(path), parents: false);
            return;
        }

        bool isDirectory;
        try
        {
            isDirectory = UnixFileType.Stat(path, followSymlinks: true) == UnixFileType.Kind.Directory;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw OsError.Create(OsError.Errno(exc, path) ?? error, path, exc);
        }

        if (!isDirectory)
        {
            throw OsError.Create(error, path);
        }
    }

    private static int MakeDirectoryNative(string path)
        => UnixMkdir.MakeDirectory(path) ?? throw new InvalidOperationException("mkdir(2) became unavailable during a call that used it.");

    /// <summary>
    /// The path <see cref="KernelPath"/> spells a result under when the directory the kernel reaches has no UTF-8 name: a name
    /// under a character device, whose lookup fails with <c>ENOTDIR</c>, so neither it nor anything below it exists, is opened
    /// or is created.
    /// </summary>
    internal const string Unreachable = "/dev/null/unreachable";

    // The failed part joined onto the directory reached before it (a ".." that failed, a link expansion past the hop limit, goes
    // under Unreachable).
    private static string UnderFailedPart(string reached, string[] components, int failedIndex)
        => string.Equals(components[failedIndex], "..", StringComparison.Ordinal)
            ? Beneath(Unreachable, components.Skip(failedIndex))
            : Beneath(Path.Join(reached, components[failedIndex]), components.Skip(failedIndex + 1));

    // parts joined under a directory that does not exist, every ".." spelled as the ordinary name "..." so the runtime's lexical
    // removal never climbs back out of it.
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
    /// <c>realpath</c>: resolves every link component of <paramref name="fullPath"/> against the physical directory
    /// resolved so far (the OS semantics <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> does not follow, since it
    /// joins a relative target with the link's lexical directory). Null for a link loop or a target that cannot be read. On
    /// Linux link targets are read as bytes (<see cref="UnixPathWalk"/>); a physical path holding a name that is not UTF-8 comes
    /// back with U+FFFD for it, text for a hash that names nothing to open (see <see cref="ResolvePhysicalPath(string, out bool)"/>).
    /// </summary>
    public static string? ResolvePhysicalPath(string fullPath) => ResolvePhysicalPath(fullPath, out _);

    /// <summary>
    /// <see cref="ResolvePhysicalPath(string)"/>; <paramref name="nameable"/> is false when the physical path holds a name that
    /// is not UTF-8, so the text returned must not be opened: the runtime would look up the U+FFFD spelling, a different entry.
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
    /// The <see cref="FileTreeGlob.Glob"/> matches under <paramref name="root"/>, sorted segment by segment and code point by code
    /// point over their posix form (<see cref="PathText.ComparePosixPaths"/>). The same case-sensitive order is used on every platform.
    /// </summary>
    /// <remarks>
    /// A root directory that exists but cannot be listed raises its I/O error rather than yielding nothing. A missing root
    /// yields nothing.
    /// </remarks>
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
