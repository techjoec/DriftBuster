namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>os.path.abspath</c>, <c>os.path.normpath</c>, <c>os.path.split</c> and <c>os.path.join</c>.</summary>
public static partial class EngineOsPath
{
    /// <summary>
    /// <c>os.path.abspath(path)</c>: a relative path joined onto the working directory, then <see cref="NormPath"/>, so ".." parts
    /// are removed lexically. On Windows this is <see cref="Path.GetFullPath(string)"/> less any trailing separator after the root.
    /// </summary>
    public static string AbsPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Length == 0 ? "." : path));
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
    /// only separators, and the text after it. On Windows the head is <see cref="Path.GetDirectoryName(string)"/> (the root when that is
    /// null) and the tail <see cref="Path.GetFileName(string)"/>.
    /// </summary>
    public static (string Head, string Tail) Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            return (Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? string.Empty, Path.GetFileName(path));
        }

        var index = path.LastIndexOf('/') + 1;
        var head = path[..index];
        if (head.Length > 0 && head.AsSpan().ContainsAnyExcept('/'))
        {
            head = head.TrimEnd('/');
        }

        return (head, path[index..]);
    }

    /// <summary>
    /// <c>os.path.join(path, name)</c>: a rooted <paramref name="name"/> replaces the path; otherwise one separator joins them. On Windows
    /// this is <see cref="Path.Combine(string, string)"/>.
    /// </summary>
    public static string Join(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(path, name);
        }

        if (name.StartsWith('/'))
        {
            return name;
        }

        return path.Length == 0 || path.EndsWith('/') ? path + name : path + "/" + name;
    }
}
