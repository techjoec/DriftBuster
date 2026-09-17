using System.Text;

using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13 <c>PurePath</c> string operations in the host platform's flavour (<c>PurePosixPath</c>, or <c>PureWindowsPath</c> on
/// Windows): <c>parts</c>, <c>str</c>, <c>parent</c>, <c>anchor</c>, <c>/</c>, <c>relative_to</c>, <c>is_absolute</c> and <c>match</c>.
/// </summary>
/// <remarks>
/// A path is parsed as <c>PurePath._parse_path</c> parses it: the flavour's <c>splitroot</c> (<see cref="PythonNtPath.SplitRoot"/> on
/// Windows, so <c>\\server\share\</c> is one anchor and a UNC drive written without the separator after the share still gets that
/// separator as its root), then the rest split on the separator with empty and <c>.</c> names dropped. It is spelled as
/// <c>_format_parsed_parts</c> spells it (anchor, then the names joined; a relative Windows path whose first name holds a drive gets a
/// leading <c>.\</c>). Each operation has an internal overload that takes the flavour, so the Windows flavour is compared with
/// CPython's <c>PureWindowsPath</c> on every host. <c>match</c> compares the pattern's parts with the path's from the right, each part
/// through <c>glob.translate(part, include_hidden=True, seps=sep)</c> compiled with <see cref="PythonPattern"/> (case-insensitively
/// on Windows); an anchored pattern must cover the whole path. <c>**</c> is an ordinary wildcard here, as it is in
/// <c>PurePath.match</c>.
/// </remarks>
public static class PythonPurePath
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    /// <summary>A parsed path: <c>drive</c>, <c>root</c> and <c>_tail</c>.</summary>
    internal readonly record struct ParsedPath(string Drive, string Root, IReadOnlyList<string> Tail)
    {
        public string Anchor => Drive + Root;
    }

    /// <summary><c>PurePath._parse_path(path)</c> in the given flavour.</summary>
    internal static ParsedPath Parse(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return new ParsedPath(string.Empty, string.Empty, []);
        }

        var separator = windows ? '\\' : '/';
        var (drive, root, rest) = SplitRoot(windows ? path.Replace('/', '\\') : path, windows);
        if (windows && root.Length == 0 && drive.StartsWith('\\') && !drive.EndsWith('\\'))
        {
            var driveParts = drive.Split('\\');
            if ((driveParts.Length == 4 && !"?.".Contains(driveParts[2], StringComparison.Ordinal)) || driveParts.Length == 6)
            {
                root = "\\";
            }
        }

        var tail = rest.Split(separator).Where(name => name.Length > 0 && !string.Equals(name, ".", StringComparison.Ordinal)).ToList();
        return new ParsedPath(drive, root, tail);
    }

    /// <summary><c>PurePath._format_parsed_parts(drv, root, tail)</c>; empty for a path with no anchor and no names.</summary>
    internal static string Format(string drive, string root, IEnumerable<string> tail, bool windows)
    {
        var separator = windows ? "\\" : "/";
        var names = tail as IReadOnlyList<string> ?? tail.ToList();
        if (drive.Length > 0 || root.Length > 0)
        {
            return drive + root + string.Join(separator, names);
        }

        return windows && names.Count > 0 && PythonNtPath.SplitDrive(names[0]).Drive.Length > 0
            ? "." + separator + string.Join(separator, names)
            : string.Join(separator, names);
    }

    private static string FormatOrDot(string drive, string root, IEnumerable<string> tail, bool windows)
    {
        var text = Format(drive, root, tail, windows);
        return text.Length == 0 ? "." : text;
    }

    /// <summary><c>PurePath(path).parts</c>.</summary>
    public static IReadOnlyList<string> Parts(string path) => Parts(path, Windows);

    internal static IReadOnlyList<string> Parts(string path, bool windows)
    {
        var parsed = Parse(path, windows);
        return parsed.Anchor.Length > 0 ? [parsed.Anchor, .. parsed.Tail] : parsed.Tail;
    }

    /// <summary><c>str(PurePath(path))</c>: redundant separators and "." components dropped, "." for an empty path.</summary>
    public static string Str(string path) => Str(path, Windows);

    internal static string Str(string path, bool windows)
    {
        var parsed = Parse(path, windows);
        return FormatOrDot(parsed.Drive, parsed.Root, parsed.Tail, windows);
    }

    /// <summary><c>PurePath(path).anchor</c>: the drive and the root.</summary>
    public static string Anchor(string path) => Parse(path, Windows).Anchor;

    /// <summary><c>PurePath(path).is_absolute()</c>: a posix path starting with "/"; a Windows path that <c>ntpath.isabs</c> accepts.</summary>
    public static bool IsAbsolute(string path) => IsAbsolute(path, Windows);

    internal static bool IsAbsolute(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        return windows ? PythonNtPath.IsAbs(Str(path, windows)) : path.StartsWith('/');
    }

    /// <summary>
    /// <c>str(PurePosixPath(path))</c> on every platform: "/" is the only separator, empty and "." components are dropped, a
    /// leading "//" (exactly two slashes) is kept as the anchor, and an empty result is ".".
    /// </summary>
    public static string PosixStr(string path) => Str(path, windows: false);

    /// <summary>
    /// The flavour's <c>os.path.splitroot(path)</c>: <see cref="PythonNtPath.SplitRoot"/> on Windows; on posix no drive, a root of
    /// "//" when the path starts with exactly two slashes, "/" for one or three and more, else none.
    /// </summary>
    internal static (string Drive, string Root, string Remainder) SplitRoot(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (windows)
        {
            return PythonNtPath.SplitRoot(path);
        }

        var root = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal)
            ? "//"
            : path.StartsWith('/') ? "/" : string.Empty;
        return (string.Empty, root, path[root.Length..]);
    }

    /// <summary><c>str(PurePath(path).parent)</c>: the path less its last name; the path itself when it has no names.</summary>
    public static string Parent(string path) => Parent(path, Windows);

    internal static string Parent(string path, bool windows)
    {
        var parsed = Parse(path, windows);
        return parsed.Tail.Count == 0
            ? FormatOrDot(parsed.Drive, parsed.Root, parsed.Tail, windows)
            : FormatOrDot(parsed.Drive, parsed.Root, parsed.Tail.Take(parsed.Tail.Count - 1), windows);
    }

    /// <summary>
    /// <c>str(PurePath(path) / relative)</c>: the two joined with the flavour's <c>os.path.join</c> (an anchored
    /// <paramref name="relative"/> replaces the path, a Windows root-relative one keeps the path's drive), then parsed.
    /// </summary>
    public static string Join(string path, string relative) => Join(path, relative, Windows);

    internal static string Join(string path, string relative, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(relative);
        if (windows)
        {
            return Str(PythonNtPath.Join(path, relative), windows);
        }

        var joined = relative.StartsWith('/') || path.Length == 0 ? relative : path.EndsWith('/') ? path + relative : path + "/" + relative;
        return Str(joined, windows);
    }

    /// <summary>
    /// <c>PurePath(path).relative_to(root).as_posix()</c>, or null where Python raises <c>ValueError</c>: the root must equal the
    /// path or one of its parents, compared as <c>str()</c> (lower-cased with <c>str.lower</c> on Windows).
    /// </summary>
    public static string? RelativeTo(string path, string root) => RelativeTo(path, root, Windows);

    internal static string? RelativeTo(string path, string root, bool windows)
    {
        var self = Parse(path, windows);
        var other = Parse(root, windows);
        var otherKey = NormCase(FormatOrDot(other.Drive, other.Root, other.Tail, windows), windows);
        var related = Enumerable.Range(0, self.Tail.Count + 1)
            .Select(kept => NormCase(FormatOrDot(self.Drive, self.Root, self.Tail.Take(kept), windows), windows))
            .Contains(otherKey, StringComparer.Ordinal);
        if (!related)
        {
            return null;
        }

        var text = FormatOrDot(string.Empty, string.Empty, self.Tail.Skip(other.Tail.Count), windows);
        return windows ? text.Replace('\\', '/') : text;
    }

    // PurePath._str_normcase: str(self), lower-cased on Windows.
    private static string NormCase(string text, bool windows) => windows ? PythonText.Lower(text) : text;

    /// <summary><c>PurePath(path).match(pattern)</c>.</summary>
    public static bool Match(string path, string pattern) => Match(path, pattern, Windows);

    internal static bool Match(string path, string pattern, bool windows)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var pathParts = Parts(path, windows).Reverse().ToList();
        var parsedPattern = Parse(pattern, windows);
        var patternParts = Parts(pattern, windows).Reverse().ToList();
        if (patternParts.Count == 0)
        {
            throw new PythonValueException("empty pattern", nameof(pattern));
        }

        if (pathParts.Count < patternParts.Count || (pathParts.Count > patternParts.Count && parsedPattern.Anchor.Length > 0))
        {
            return false;
        }

        var flags = windows ? PythonReFlags.IgnoreCase : PythonReFlags.None;
        var separator = windows ? "\\" : "/";
        for (var index = 0; index < patternParts.Count; index++)
        {
            var compiled = PythonPattern.Compile(GlobTranslate(patternParts[index], separator), flags);
            if (compiled.Match(pathParts[index]) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>glob.translate(pat, recursive=False, include_hidden=True, seps=separator)</c>.</summary>
    internal static string GlobTranslate(string pattern, string separator)
    {
        var escapedSeparator = Escape(separator);
        var notSeparator = "[^" + escapedSeparator + "]";
        var parts = pattern.Split(separator);
        var results = new StringBuilder();
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var last = index == parts.Length - 1;
            if (string.Equals(part, "*", StringComparison.Ordinal))
            {
                results.Append(notSeparator).Append('+');
                if (!last)
                {
                    results.Append(escapedSeparator);
                }

                continue;
            }

            if (part.Length > 0)
            {
                results.Append(FnmatchTranslate(part, notSeparator + "*", notSeparator));
            }

            if (!last)
            {
                results.Append(escapedSeparator);
            }
        }

        return "(?s:" + results + ")\\Z";
    }

    /// <summary><c>fnmatch._translate(pat, STAR, QUESTION_MARK)</c> joined, with consecutive stars compressed.</summary>
    private static string FnmatchTranslate(string pattern, string star, string questionMark)
        => string.Concat(FnmatchTranslateParts(pattern, star, questionMark));

    /// <summary>
    /// <c>fnmatch._translate(pat, STAR, QUESTION_MARK)</c>: the translated pieces, with <paramref name="star"/> (compared by
    /// reference, so pass a unique instance) standing for each run of consecutive stars.
    /// </summary>
    internal static List<string> FnmatchTranslateParts(string pattern, string star, string questionMark)
    {
        var chars = ReTokenizer.CodePoints(pattern).Select(code => ReTokenizer.Text([code])).ToList();
        var result = new List<string>();
        var i = 0;
        var n = chars.Count;
        while (i < n)
        {
            var c = chars[i];
            i++;
            if (string.Equals(c, "*", StringComparison.Ordinal))
            {
                if (result.Count == 0 || !ReferenceEquals(result[^1], star))
                {
                    result.Add(star);
                }
            }
            else if (string.Equals(c, "?", StringComparison.Ordinal))
            {
                result.Add(questionMark);
            }
            else if (string.Equals(c, "[", StringComparison.Ordinal))
            {
                i = TranslateBracket(chars, i, result);
            }
            else
            {
                result.Add(Escape(c));
            }
        }

        return result;
    }

    private static int TranslateBracket(List<string> chars, int i, List<string> result)
    {
        var n = chars.Count;
        var j = i;
        if (j < n && string.Equals(chars[j], "!", StringComparison.Ordinal))
        {
            j++;
        }

        if (j < n && string.Equals(chars[j], "]", StringComparison.Ordinal))
        {
            j++;
        }

        while (j < n && !string.Equals(chars[j], "]", StringComparison.Ordinal))
        {
            j++;
        }

        if (j >= n)
        {
            result.Add("\\[");
            return i;
        }

        var stuff = BracketBody(chars, i, j);
        stuff = new StringBuilder(stuff).Replace("&", "\\&").Replace("~", "\\~").Replace("|", "\\|").ToString();
        if (stuff.Length == 0)
        {
            result.Add("(?!)");
        }
        else if (string.Equals(stuff, "!", StringComparison.Ordinal))
        {
            result.Add(".");
        }
        else
        {
            if (stuff[0] == '!')
            {
                stuff = "^" + stuff[1..];
            }
            else if (stuff[0] is '^' or '[')
            {
                stuff = "\\" + stuff;
            }

            result.Add("[" + stuff + "]");
        }

        return j + 1;
    }

    // The set text between "[" (at i) and "]" (at j): backslashes escaped, ranges kept, empty ranges removed and
    // hyphens that do not form a range escaped.
    private static string BracketBody(List<string> chars, int i, int j)
    {
        string Slice(int from, int to) => string.Concat(chars.Skip(from).Take(to - from));
        if (!chars.Skip(i).Take(j - i).Contains("-", StringComparer.Ordinal))
        {
            return Slice(i, j).Replace("\\", "\\\\", StringComparison.Ordinal);
        }

        var chunks = new List<List<string>>();
        var k = string.Equals(chars[i], "!", StringComparison.Ordinal) ? i + 2 : i + 1;
        while (k < j)
        {
            k = chars.FindIndex(k, j - k, ch => string.Equals(ch, "-", StringComparison.Ordinal));
            if (k < 0)
            {
                break;
            }

            chunks.Add(chars.Skip(i).Take(k - i).ToList());
            i = k + 1;
            k += 3;
        }

        var tail = chars.Skip(i).Take(j - i).ToList();
        if (tail.Count > 0)
        {
            chunks.Add(tail);
        }
        else
        {
            chunks[^1].Add("-");
        }

        for (var index = chunks.Count - 1; index > 0; index--)
        {
            if (PathText.CompareCodePoints(chunks[index - 1][^1], chunks[index][0]) > 0)
            {
                chunks[index - 1] = [.. chunks[index - 1].Take(chunks[index - 1].Count - 1), .. chunks[index].Skip(1)];
                chunks.RemoveAt(index);
            }
        }

        return string.Join("-", chunks.Select(chunk => string.Concat(chunk).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("-", "\\-", StringComparison.Ordinal)));
    }

    /// <summary><c>re.escape</c>: backslash before each of <c>()[]{}?*+-|^$\.&amp;~#</c> and the whitespace characters.</summary>
    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if ("()[]{}?*+-|^$\\.&~# \t\n\r\v\f".Contains(ch, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
