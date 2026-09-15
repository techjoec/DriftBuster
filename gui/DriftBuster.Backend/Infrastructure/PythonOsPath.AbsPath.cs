namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>os.path.abspath</c>, <c>os.path.normpath</c>, <c>os.path.split</c> and <c>os.path.join</c>.</summary>
public static partial class PythonOsPath
{
    /// <summary>
    /// <c>os.path.abspath(path)</c>: a relative path joined onto the working directory, then <see cref="NormPath"/>, so ".." parts
    /// are removed lexically. On Windows the runtime's full-path resolution stands in for <c>GetFullPathNameW</c>.
    /// </summary>
    public static string AbsPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            // ntpath.abspath: normpath (which drops trailing separators) and then GetFullPathNameW.
            var full = Path.GetFullPath(path.Length == 0 ? "." : path);
            var anchor = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > anchor.Length ? full.TrimEnd('\\', '/') : full;
        }

        return NormPath(path.StartsWith('/') ? path : Join(Directory.GetCurrentDirectory(), path));
    }

    /// <summary>
    /// <c>posixpath.normpath(path)</c>: empty and "." parts dropped, ".." removing the part before it (kept at the start of a relative
    /// path), a leading "//" (exactly two slashes) kept, and "." for an empty result.
    /// </summary>
    public static string NormPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var initialSlashes = path.StartsWith('/') ? 1 : 0;
        if (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal))
        {
            initialSlashes = 2;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(part, "..", StringComparison.Ordinal) || (initialSlashes == 0 && parts.Count == 0)
                || (parts.Count > 0 && string.Equals(parts[^1], "..", StringComparison.Ordinal)))
            {
                parts.Add(part);
            }
            else if (parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var joined = new string('/', initialSlashes) + string.Join('/', parts);
        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>
    /// <c>os.path.split(path)</c>: the text up to and including the last separator, with trailing separators removed unless it is
    /// only separators (or the drive and root on Windows), and the text after it.
    /// </summary>
    public static (string Head, string Tail) Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            var anchor = Path.GetPathRoot(path) ?? string.Empty;
            var rest = path[anchor.Length..];
            var cut = rest.Length;
            while (cut > 0 && rest[cut - 1] is not ('/' or '\\'))
            {
                cut--;
            }

            return (anchor + rest[..cut].TrimEnd('/', '\\'), rest[cut..]);
        }

        var index = path.LastIndexOf('/') + 1;
        var head = path[..index];
        if (head.Length > 0 && head.AsSpan().ContainsAnyExcept('/'))
        {
            head = head.TrimEnd('/');
        }

        return (head, path[index..]);
    }

    /// <summary><c>os.path.join(path, name)</c>: a rooted <paramref name="name"/> replaces the path; otherwise one separator joins them.</summary>
    public static string Join(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        if (OperatingSystem.IsWindows())
        {
            return Path.IsPathRooted(name) || path.Length == 0 ? name : path[^1] is '/' or '\\' or ':' ? path + name : path + "\\" + name;
        }

        if (name.StartsWith('/'))
        {
            return name;
        }

        return path.Length == 0 || path.EndsWith('/') ? path + name : path + "/" + name;
    }
}
