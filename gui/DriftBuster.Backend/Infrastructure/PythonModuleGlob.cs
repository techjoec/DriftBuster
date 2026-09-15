namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>glob.glob(pathname, recursive=...)</c> as CPython 3.13's <c>glob</c> module runs it (PSF License), with no <c>root_dir</c>
/// or <c>dir_fd</c> and <c>include_hidden=False</c>. It differs from <c>Path.glob</c> (<see cref="PythonGlob"/>): the pattern
/// is split with <c>os.path.split</c> rather than parsed as a path, a name starting with "." matches a wildcard only when the
/// pattern part starts with "." too, <c>**</c> (when recursive) follows symlinked directories, and results come in directory
/// listing order, unsorted.
/// </summary>
/// <remarks>
/// A directory that cannot be listed contributes no names. A literal part is checked with <c>os.path.lexists</c> (a dangling
/// link exists); a pattern ending in a separator matches directories only. The cancellation token is checked before every
/// directory is listed. A path holding an unpaired surrogate is never looked up or listed (it does not exist and lists no names):
/// the runtime would reach the entry its U+FFFD spelling names (phase 5 decision R). Python looks a <c>surrogateescape</c> spelling
/// up by its byte and raises <c>UnicodeEncodeError</c> listing a directory spelled with any other lone surrogate. A listed name the runtime
/// decoded with U+FFFD (a Linux name that is not UTF-8, which Python lists as its <c>surrogateescape</c> spelling) is left out unless an
/// entry of that spelling exists, and is listed once, so an entry whose name really holds U+FFFD never stands in for such a sibling.
/// </remarks>
public static class PythonModuleGlob
{
    /// <summary>The paths <c>glob.glob(pathname, recursive=recursive)</c> returns, in its order.</summary>
    public static IReadOnlyList<string> Glob(string pathname, bool recursive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathname);
        var results = IGlob(pathname, recursive, dirOnly: false, cancellationToken).ToList();
        // iglob skips the empty string _glob2 yields first for a pattern starting with "**" (or an empty pattern).
        if ((pathname.Length == 0 || (recursive && IsRecursive(pathname[..Math.Min(2, pathname.Length)]))) && results.Count > 0 && results[0].Length == 0)
        {
            results.RemoveAt(0);
        }

        return results;
    }

    /// <summary><c>glob.has_magic(s)</c>: the text holds <c>*</c>, <c>?</c> or <c>[</c>.</summary>
    public static bool HasMagic(string text) => text.AsSpan().IndexOfAny("*?[") >= 0;

    private static bool IsRecursive(string pattern) => string.Equals(pattern, "**", StringComparison.Ordinal);

    private static bool IsHidden(string name) => name.StartsWith('.');

    // _iglob(pathname, root_dir="", dir_fd=None, recursive, dironly).
    private static IEnumerable<string> IGlob(string pathname, bool recursive, bool dirOnly, CancellationToken cancellationToken)
    {
        var (dirname, basename) = PythonOsPath.Split(pathname);
        if (!HasMagic(pathname))
        {
            if (basename.Length > 0 ? LExists(pathname) : IsDir(dirname))
            {
                yield return pathname;
            }

            yield break;
        }

        if (dirname.Length == 0)
        {
            var names = recursive && IsRecursive(basename)
                ? Glob2(string.Empty, dirOnly, cancellationToken)
                : Glob1(string.Empty, basename, dirOnly, cancellationToken);
            foreach (var name in names)
            {
                yield return name;
            }

            yield break;
        }

        IEnumerable<string> dirs = !string.Equals(dirname, pathname, StringComparison.Ordinal) && HasMagic(dirname)
            ? IGlob(dirname, recursive, dirOnly: true, cancellationToken)
            : [dirname];
        foreach (var directory in dirs)
        {
            IEnumerable<string> names = !HasMagic(basename)
                ? Glob0(directory, basename)
                : recursive && IsRecursive(basename)
                    ? Glob2(directory, dirOnly, cancellationToken)
                    : Glob1(directory, basename, dirOnly, cancellationToken);
            foreach (var name in names)
            {
                yield return PythonOsPath.Join(directory, name);
            }
        }
    }

    // _glob1: the listed names (hidden ones only for a hidden pattern) that fnmatch.filter keeps.
    private static List<string> Glob1(string dirname, string pattern, bool dirOnly, CancellationToken cancellationToken)
        => ListDir(dirname, dirOnly, cancellationToken)
            .Where(name => IsHidden(pattern) || !IsHidden(name))
            .Where(name => PythonFnmatch.Fnmatch(name, pattern))
            .ToList();

    // _glob0: the literal name when it exists; an empty name (a pattern ending in a separator) when the directory exists.
    private static List<string> Glob0(string dirname, string basename)
    {
        if (basename.Length > 0)
        {
            return LExists(JoinParts(dirname, basename)) ? [basename] : [];
        }

        return IsDir(dirname) ? [basename] : [];
    }

    // _glob2: "" for the directory itself (when it exists), then every visible name below it, depth first (_rlistdir).
    private static IEnumerable<string> Glob2(string dirname, bool dirOnly, CancellationToken cancellationToken)
    {
        if (dirname.Length == 0 || IsDir(dirname))
        {
            yield return string.Empty;
        }

        var stack = new Stack<(string Path, string Relative, List<string> Names, int Next)>();
        stack.Push((dirname, string.Empty, ListDir(dirname, dirOnly, cancellationToken), 0));
        while (stack.Count > 0)
        {
            var (path, relative, names, next) = stack.Pop();
            if (next >= names.Count)
            {
                continue;
            }

            stack.Push((path, relative, names, next + 1));
            var name = names[next];
            if (IsHidden(name))
            {
                continue;
            }

            var yielded = relative.Length == 0 ? name : JoinParts(relative, name);
            yield return yielded;
            var child = path.Length == 0 ? name : JoinParts(path, name);
            stack.Push((child, yielded, ListDir(child, dirOnly, cancellationToken), 0));
        }
    }

    // _join: either part alone when the other is empty, else os.path.join.
    private static string JoinParts(string dirname, string basename)
        => dirname.Length == 0 || basename.Length == 0 ? dirname + basename : PythonOsPath.Join(dirname, basename);

    // _listdir / _iterdir: os.scandir(dirname or "."), every name (only directories after following links when dirOnly); an error
    // listing the directory yields nothing.
    private static List<string> ListDir(string dirname, bool dirOnly, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = dirname.Length == 0 ? "." : dirname;
        if (PythonUtf8.HasUnpairedSurrogate(directory))
        {
            return [];
        }

        try
        {
            var names = ListedNames(directory, Directory.EnumerateFileSystemEntries(PythonPath.KernelPath(directory)).Select(Path.GetFileName).OfType<string>());
            return dirOnly ? names.Where(name => IsDir(PythonOsPath.Join(directory, name))).ToList() : names;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return [];
        }
    }

    // The names of a listing less each name the runtime decoded with U+FFFD that names no entry (a Linux name that is not UTF-8, which
    // Python lists as its surrogateescape spelling), and less every repeat of such a name, so an entry whose name really holds U+FFFD is
    // listed once and never stands in for an undecodable sibling (platform limit, decision R).
    private static List<string> ListedNames(string directory, IEnumerable<string> names)
    {
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        return names.Where(name => !name.Contains('\uFFFD', StringComparison.Ordinal) || (replaced.Add(name) && LExists(PythonOsPath.Join(directory, name)))).ToList();
    }

    // os.path.lexists: an lstat succeeds.
    private static bool LExists(string path)
    {
        if (PythonUtf8.HasUnpairedSurrogate(path))
        {
            return false;
        }

        try
        {
            if (UnixFileType.Stat(path, followSymlinks: false) is { } kind)
            {
                return kind != UnixFileType.Kind.Missing;
            }

            var kernel = PythonPath.KernelPath(path);
            return File.Exists(kernel) || Directory.Exists(kernel) || new FileInfo(kernel).LinkTarget is not null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // os.path.isdir: a stat that follows links reports a directory; any error is False.
    private static bool IsDir(string path)
    {
        if (PythonUtf8.HasUnpairedSurrogate(path))
        {
            return false;
        }

        try
        {
            if (UnixFileType.Stat(path, followSymlinks: true) is { } kind)
            {
                return kind == UnixFileType.Kind.Directory;
            }

            return Directory.Exists(PythonPath.KernelPath(path));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
