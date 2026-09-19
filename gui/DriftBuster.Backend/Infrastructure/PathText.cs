namespace DriftBuster.Backend.Infrastructure;

/// <summary>Path text rules DriftBuster shows and sorts by, over <see cref="LexicalPath"/>.</summary>
public static class PathText
{
    /// <summary>The last segment (empty and "." segments dropped); "", for ".", "/" or a bare root.</summary>
    public static string Name(string path) => LexicalPath.Name(path);

    /// <summary>
    /// Labels for two paths shown side by side: their file names, or, when the names are the same, each path from the
    /// nearest folder that tells them apart ("C:\a\baseline\web.config" and "\\host\C$\a\prod\web.config" read
    /// "baseline/web.config" and "prod/web.config").
    /// </summary>
    public static (string Left, string Right) DistinctNames(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftName = Name(left);
        var rightName = Name(right);
        if (!string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase))
        {
            return (leftName, rightName);
        }

        var leftParts = left.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var shared = 0;
        while (shared < leftParts.Length && shared < rightParts.Length
            && string.Equals(leftParts[^(shared + 1)], rightParts[^(shared + 1)], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        string Label(string[] parts) => string.Join('/', parts[Math.Max(parts.Length - shared - 1, 0)..]);
        return (Label(leftParts), Label(rightParts));
    }

    public static string NameLower(string path) => Name(path).ToLowerInvariant();

    /// <summary>
    /// The final suffix: the last dot must be neither the first nor the last character of the name, so ".env" and "foo." have no
    /// suffix while ".env.local" has ".local".
    /// </summary>
    public static string Suffix(string path)
    {
        var name = Name(path);
        var index = name.LastIndexOf('.');
        return index > 0 && index < name.Length - 1 ? name[index..] : string.Empty;
    }

    public static string SuffixLower(string path) => Suffix(path).ToLowerInvariant();

    public static string ToPosix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.DirectorySeparatorChar == '/' ? path : path.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string RelativePosix(string root, string path) => ToPosix(Path.GetRelativePath(root, path));

    /// <summary>
    /// Orders posix paths component by component, each ordinally: "a/z" before "a-b", so a folder's entries stay together.
    /// </summary>
    public static int ComparePosixPaths(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftParts = left.Split('/');
        var rightParts = right.Split('/');
        var count = Math.Min(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var result = string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}
