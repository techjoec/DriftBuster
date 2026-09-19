namespace DriftBuster.Backend.Infrastructure;

/// <summary>The path text helpers the engine uses, over <see cref="LexicalPath"/>; engine code never calls <see cref="Path.GetExtension"/>.</summary>
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
    /// Orders posix paths component by component, each by code point: "a/z" before "a-b", and astral characters after all BMP ones.
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
            var result = CompareCodePoints(leftParts[index], rightParts[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>
    /// Ordering by code point, so astral characters sort after every BMP character. A surrogate pair is one astral code point; an
    /// unpaired surrogate is its own code point (U+D800-U+DFFF), below U+E000.
    /// </summary>
    public static int CompareCodePoints(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var a = CodePointAt(left, leftIndex, out var leftWidth);
            var b = CodePointAt(right, rightIndex, out var rightWidth);
            if (a != b)
            {
                return a.CompareTo(b);
            }

            leftIndex += leftWidth;
            rightIndex += rightWidth;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    // The code point starting at index: a high surrogate followed by a low one is one astral code point, any other unit is its own.
    private static int CodePointAt(string text, int index, out int width)
    {
        var ch = text[index];
        if (char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return char.ConvertToUtf32(ch, text[index + 1]);
        }

        width = 1;
        return ch;
    }
}
