namespace DriftBuster.Backend.Infrastructure;

/// <summary>Path text helpers with Python <c>pathlib</c> semantics; ported code never calls <see cref="Path.GetExtension"/>.</summary>
public static class PathText
{
    private static readonly char[] Separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
        ? [Path.DirectorySeparatorChar]
        : [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>The final path component as <c>PurePath.name</c> returns it: trailing separators ignored, "." and "/" give "".</summary>
    public static string Name(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd(Separators);
        var name = Path.GetFileName(trimmed);
        return string.Equals(name, ".", StringComparison.Ordinal) ? string.Empty : name;
    }

    /// <summary><see cref="Name"/> lowered with the invariant culture, matching <c>path.name.lower()</c>.</summary>
    public static string NameLower(string path) => PythonText.Lower(Name(path));

    /// <summary>
    /// The final suffix as <c>PurePath.suffix</c> returns it: the last dot must be neither the first nor the last
    /// character of the name, so ".env" and "foo." have no suffix while ".env.local" has ".local".
    /// </summary>
    public static string Suffix(string path)
    {
        var name = Name(path);
        var index = name.LastIndexOf('.');
        return index > 0 && index < name.Length - 1 ? name[index..] : string.Empty;
    }

    /// <summary><see cref="Suffix"/> lowered with the invariant culture, matching <c>path.suffix.lower()</c>.</summary>
    public static string SuffixLower(string path) => PythonText.Lower(Suffix(path));

    /// <summary>Replaces the platform directory separator with "/", matching <c>PurePath.as_posix()</c>.</summary>
    public static string ToPosix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.DirectorySeparatorChar == '/' ? path : path.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>The posix-style path of <paramref name="path"/> relative to <paramref name="root"/>.</summary>
    public static string RelativePosix(string root, string path) => ToPosix(Path.GetRelativePath(root, path));

    /// <summary>
    /// Orders posix-style paths the way <c>sorted()</c> orders <c>PurePosixPath</c> objects: component by component,
    /// each component compared by Unicode code point. "a/z" sorts before "a-b" and "a.txt" because the
    /// component "a" is shorter, and U+FF5E sorts before U+1F600 because code points, not UTF-16 units, are compared.
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
    /// Python <c>str</c> ordering: by code point, so astral characters sort after every BMP character. A surrogate pair is
    /// one astral code point; an unpaired surrogate is its own code point (U+D800-U+DFFF), below U+E000.
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
