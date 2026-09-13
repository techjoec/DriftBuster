using System.Text;

using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13 <c>PurePath.parts</c> and <c>PurePath.match</c> for the host platform: posix paths split on "/" and match
/// case-sensitively; Windows paths split on both separators after the drive/root anchor and match case-insensitively.
/// </summary>
/// <remarks>
/// <c>match</c> compares the pattern's parts with the path's from the right, each part through
/// <c>glob.translate(part, include_hidden=True, seps=sep)</c> compiled with <see cref="PythonPattern"/>; an anchored
/// pattern must cover the whole path. <c>**</c> is an ordinary wildcard here, as it is in <c>PurePath.match</c>.
/// </remarks>
public static class PythonPurePath
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    /// <summary><c>PurePath(path).parts</c>.</summary>
    public static IReadOnlyList<string> Parts(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = new List<string>();
        string rest;
        if (Windows)
        {
            var anchor = Path.GetPathRoot(path) ?? string.Empty;
            if (anchor.Length > 0)
            {
                parts.Add(anchor.Replace('/', '\\'));
            }

            rest = path[anchor.Length..];
        }
        else
        {
            var anchor = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal)
                ? "//"
                : path.StartsWith('/') ? "/" : string.Empty;
            if (anchor.Length > 0)
            {
                parts.Add(anchor);
            }

            rest = path.TrimStart('/');
        }

        var separators = Windows ? new[] { '/', '\\' } : ['/'];
        parts.AddRange(rest.Split(separators, StringSplitOptions.RemoveEmptyEntries).Where(part => !string.Equals(part, ".", StringComparison.Ordinal)));
        return parts;
    }

    /// <summary><c>str(PurePath(path))</c>: redundant separators and "." components dropped, "." for an empty path.</summary>
    public static string Str(string path)
    {
        var parts = Parts(path);
        if (parts.Count == 0)
        {
            return ".";
        }

        var separator = Windows ? "\\" : "/";
        return HasAnchor(path)
            ? parts[0] + string.Join(separator, parts.Skip(1))
            : string.Join(separator, parts);
    }

    /// <summary><c>str(PurePath(path).parent)</c>: the path less its last part; "." for a single relative part.</summary>
    public static string Parent(string path)
    {
        var parts = Parts(path);
        var anchored = HasAnchor(path);
        if (parts.Count <= 1)
        {
            return anchored && parts.Count == 1 ? parts[0] : ".";
        }

        var separator = Windows ? "\\" : "/";
        var kept = parts.Take(parts.Count - 1).ToList();
        return anchored ? kept[0] + string.Join(separator, kept.Skip(1)) : string.Join(separator, kept);
    }

    /// <summary><c>PurePath(path) / relative</c> as a string, <paramref name="relative"/> being posix-style.</summary>
    public static string Join(string path, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var root = Str(path);
        var tail = Windows ? relative.Replace('/', '\\') : relative;
        if (string.Equals(root, ".", StringComparison.Ordinal))
        {
            return Str(tail);
        }

        var separator = Windows ? "\\" : "/";
        return Str(root.EndsWith(separator, StringComparison.Ordinal) ? root + tail : root + separator + tail);
    }

    /// <summary>
    /// <c>PurePath(path).relative_to(root).as_posix()</c>, or null where Python raises <c>ValueError</c> (the root's parts
    /// are not a prefix of the path's).
    /// </summary>
    public static string? RelativeTo(string path, string root)
    {
        var pathParts = Parts(path);
        var rootParts = Parts(root);
        var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (rootParts.Count > pathParts.Count || rootParts.Where((part, index) => !string.Equals(part, pathParts[index], comparison)).Any())
        {
            return null;
        }

        var remaining = pathParts.Skip(rootParts.Count).ToList();
        return remaining.Count == 0 ? "." : string.Join('/', remaining);
    }

    private static bool HasAnchor(string path) => Windows ? (Path.GetPathRoot(path) ?? string.Empty).Length > 0 : path.StartsWith('/');

    /// <summary><c>PurePath(path).match(pattern)</c>.</summary>
    public static bool Match(string path, string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var pathParts = Parts(path).Reverse().ToList();
        var patternParts = Parts(pattern).Reverse().ToList();
        if (patternParts.Count == 0)
        {
            throw new PythonValueException("empty pattern", nameof(pattern));
        }

        if (pathParts.Count < patternParts.Count)
        {
            return false;
        }

        var anchored = Windows ? Path.IsPathRooted(pattern) : pattern.StartsWith('/');
        if (pathParts.Count > patternParts.Count && anchored)
        {
            return false;
        }

        var flags = Windows ? PythonReFlags.IgnoreCase : PythonReFlags.None;
        var separator = Windows ? "\\" : "/";
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

        return string.Concat(result);
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
    private static string Escape(string text)
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
