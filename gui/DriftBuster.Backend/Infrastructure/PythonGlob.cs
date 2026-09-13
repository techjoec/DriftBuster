using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>pathlib.Path(root).glob(pattern)</c> as CPython 3.13 runs it (<c>Path.glob</c> over <c>glob._StringGlobber</c>, PSF
/// License): the same selectors, the same file-system queries and the same result strings.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The pattern is parsed as a <c>PurePath</c>: redundant separators and <c>.</c> parts disappear, a trailing separator
/// adds an empty part, an empty pattern raises <see cref="ArgumentException"/> (<c>ValueError("Unacceptable pattern")</c>)
/// and an anchored one <see cref="NotSupportedException"/> (<c>NotImplementedError</c>).</item>
/// <item>A <c>**</c> part walks the tree without following symlinked directories and also yields the directory it starts
/// from; a wildcard part lists one directory and, when more parts follow, descends into entries that are directories
/// after following symlinks; a part without <c>* ? [</c> is joined on without listing anything; <c>..</c> and the empty
/// trailing part are appended as written. Wildcards are <c>glob.translate</c> patterns (hidden files included), matched
/// case-sensitively on posix and case-insensitively on Windows.</item>
/// <item>A directory that cannot be listed yields nothing, as <c>scandir</c> errors are swallowed; the caller decides what
/// an unreadable root means. Results come in walk order, duplicates included (<c>**/a/**</c> can reach a path twice).</item>
/// <item>The cancellation token is checked before every directory is listed.</item>
/// </list>
/// </remarks>
public static class PythonGlob
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly char Separator = Windows ? '\\' : '/';

    /// <summary>The <c>str()</c> of every path <c>Path(root).glob(pattern)</c> yields, in yield order.</summary>
    public static IReadOnlyList<string> Glob(string root, string pattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(pattern);
        var parts = PatternParts(pattern);
        var rootText = PythonPurePath.Str(root);
        var anchorLength = AnchorLength(rootText);
        var results = new List<string>();
        var globber = new Globber(parts, cancellationToken);
        foreach (var path in globber.Select(0, rootText, exists: false))
        {
            var normalised = path;
            if (string.Equals(rootText, ".", StringComparison.Ordinal))
            {
                normalised = normalised[2..];
            }

            if (parts[^1].Length == 0 && normalised.Length > 0)
            {
                normalised = normalised[..^1];
            }
            else if (string.Equals(parts[^1], "**", StringComparison.Ordinal) && normalised.Length > anchorLength
                && normalised[^1] == Separator)
            {
                normalised = normalised[..^1];
            }

            results.Add(PythonPurePath.Str(normalised));
        }

        return results;
    }

    private static List<string> PatternParts(string pattern)
    {
        var pureParts = PythonPurePath.Parts(pattern);
        var anchored = Windows ? Path.IsPathRooted(pattern) : pattern.StartsWith('/');
        if (anchored)
        {
            throw new NotSupportedException("Non-relative patterns are unsupported");
        }

        var parts = pureParts.ToList();
        if (parts.Count == 0)
        {
            var repr = (Windows ? "WindowsPath(" : "PosixPath(") + PythonRepr.StrRepr(PythonPurePath.Str(pattern)) + ")";
            throw new PythonValueException("Unacceptable pattern: " + repr, nameof(pattern));
        }

        if (pattern[^1] == '/' || (Windows && pattern[^1] == '\\'))
        {
            parts.Add(string.Empty);
        }

        return parts;
    }

    private static int AnchorLength(string path) => Windows ? (Path.GetPathRoot(path) ?? string.Empty).Length : path.StartsWith('/') ? 1 : 0;

    private static bool IsSpecial(string part) => part is "" or "." or "..";

    private static bool HasMagic(string part) => part.AsSpan().IndexOfAny("*?[") >= 0;

    // _Globber: the parsed pattern parts and the caller's cancellation token, shared by every selector.
    private sealed class Globber(List<string> parts, CancellationToken cancellationToken)
    {
        // _Globber.selector: dispatches on the part at index, consuming the parts the selector joins.
        public IEnumerable<string> Select(int index, string path, bool exists)
        {
            if (index == parts.Count)
            {
                return SelectExists(path, exists);
            }

            var part = parts[index];
            if (string.Equals(part, "**", StringComparison.Ordinal))
            {
                var next = index + 1;
                while (next < parts.Count && string.Equals(parts[next], "**", StringComparison.Ordinal))
                {
                    next++;
                }

                return SelectRecursive(next, path, exists);
            }

            if (IsSpecial(part))
            {
                return Select(index + 1, AddSlash(path) + part, exists);
            }

            if (!HasMagic(part))
            {
                var next = index + 1;
                while (next < parts.Count && !HasMagic(parts[next]))
                {
                    part += Separator + parts[next];
                    next++;
                }

                return Select(next, AddSlash(path) + part, exists: false);
            }

            return SelectWildcard(index, path);
        }

        private static IEnumerable<string> SelectExists(string path, bool exists)
        {
            if (exists || File.Exists(path) || Directory.Exists(path))
            {
                yield return path;
            }
        }

        private IEnumerable<string> SelectWildcard(int index, string path)
        {
            var part = parts[index];
            var match = string.Equals(part, "*", StringComparison.Ordinal)
                ? null
                : PythonPattern.Compile(PythonPurePath.GlobTranslate(part, Separator.ToString()), Windows ? PythonReFlags.IgnoreCase : PythonReFlags.None);
            var dirOnly = index + 1 < parts.Count;
            foreach (var name in ScanDirectory(path))
            {
                if (match is not null && match.Match(name) is null)
                {
                    continue;
                }

                var entryPath = JoinEntry(path, name);
                if (!dirOnly)
                {
                    yield return entryPath;
                    continue;
                }

                if (!IsDirectory(entryPath, followSymlinks: true))
                {
                    continue;
                }

                foreach (var selected in Select(index + 1, entryPath, exists: true))
                {
                    yield return selected;
                }
            }
        }

        private IEnumerable<string> SelectRecursive(int next, string path, bool exists)
        {
            path = AddSlash(path);
            var dirOnly = next < parts.Count;
            foreach (var selected in Select(next, path, exists))
            {
                yield return selected;
            }

            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                foreach (var name in ScanDirectory(directory))
                {
                    var entryPath = JoinEntry(directory, name);
                    var isDirectory = IsDirectory(entryPath, followSymlinks: false);
                    if (isDirectory || !dirOnly)
                    {
                        if (dirOnly)
                        {
                            foreach (var selected in Select(next, entryPath, exists: true))
                            {
                                yield return selected;
                            }
                        }
                        else
                        {
                            yield return entryPath;
                        }
                    }

                    if (isDirectory)
                    {
                        stack.Push(entryPath);
                    }
                }
            }
        }

        // The names os.scandir(path) lists, in full before any entry is used; an error yields no entries. Only names are read, so
        // a directory that can be listed but not searched still lists its entries (a FileSystemInfo would stat each one).
        private List<string> ScanDirectory(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return [.. Directory.EnumerateFileSystemEntries(path.Length == 0 ? "." : path).Select(Path.GetFileName).OfType<string>()];
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return [];
            }
        }
    }

    // DirEntry.is_dir(follow_symlinks=...): False for a link when not following, False on an error.
    private static bool IsDirectory(string path, bool followSymlinks)
    {
        try
        {
            if (followSymlinks)
            {
                return Directory.Exists(path);
            }

            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // _StringGlobber.add_slash.
    private static string AddSlash(string path)
    {
        if (Windows)
        {
            var tail = path[(Path.GetPathRoot(path) ?? string.Empty).Length..];
            return tail.Length == 0 || tail[^1] is '\\' or '/' ? path : path + "\\";
        }

        return path.Length == 0 || path[^1] == '/' ? path : path + "/";
    }

    // DirEntry.path: os.path.join(scandir_path, name).
    private static string JoinEntry(string directory, string name)
        => directory.Length == 0 || directory[^1] == Separator || (Windows && directory[^1] == '/') ? directory + name : directory + Separator + name;
}
