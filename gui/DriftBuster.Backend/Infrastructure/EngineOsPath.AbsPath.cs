namespace DriftBuster.Backend.Infrastructure;

/// <summary>Lexical path operations: absolute, normalise, split and join.</summary>
public static partial class EngineOsPath
{
    /// <summary>
    /// Joins a relative path onto the working directory and removes <c>..</c> lexically (<see cref="NormPath"/>). On Windows,
    /// <see cref="Path.GetFullPath(string)"/> without a trailing separator after the root.
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
    /// Drops empty and "." parts, lets ".." remove the part before it (kept at the start of a relative path), keeps a leading
    /// "//" (exactly two), and returns "." for an empty result.
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
    /// Head (up to the last separator, trailing separators trimmed unless it is all separators) and tail. Windows uses
    /// <see cref="Path.GetDirectoryName(string)"/> (the root when null) and <see cref="Path.GetFileName(string)"/>.
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

    /// <summary>A rooted <paramref name="name"/> replaces the path; otherwise one separator joins them. Windows uses <see cref="Path.Combine(string, string)"/>.</summary>
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
