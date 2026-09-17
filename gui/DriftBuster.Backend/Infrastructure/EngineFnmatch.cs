using System.Text;

using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13 <c>fnmatch</c>: <c>translate</c> (interior <c>*</c> runs become atomic <c>(?&gt;.*?fixed)</c> groups),
/// <c>fnmatchcase</c> over <see cref="EnginePattern"/> (whose compile cache stands in for <c>_compile_pattern</c>'s), and
/// <c>fnmatch</c>, which applies <c>os.path.normcase</c> to both arguments first (the identity on posix; "/" to "\" and an
/// invariant lower-casing on Windows).
/// </summary>
public static class EngineFnmatch
{
    /// <summary><c>fnmatch.fnmatch(name, pat)</c>.</summary>
    public static bool Fnmatch(string name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        return OperatingSystem.IsWindows()
            ? FnmatchCase(WindowsNormCase(name), WindowsNormCase(pattern))
            : FnmatchCase(name, pattern);
    }

    /// <summary><c>fnmatch.fnmatchcase(name, pat)</c>.</summary>
    public static bool FnmatchCase(string name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        return EnginePattern.Compile(Translate(pattern)).Match(name) is not null;
    }

    /// <summary><c>fnmatch.translate(pat)</c>.</summary>
    public static string Translate(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var star = new string(['.', '*']);
        var parts = EnginePurePath.FnmatchTranslateParts(pattern, star, ".");
        return JoinTranslatedParts(parts, star);
    }

    // fnmatch._join_translated_parts.
    private static string JoinTranslatedParts(List<string> parts, string star)
    {
        var result = new StringBuilder();
        var i = 0;
        var n = parts.Count;
        while (i < n && !ReferenceEquals(parts[i], star))
        {
            result.Append(parts[i]);
            i++;
        }

        while (i < n)
        {
            i++;
            if (i == n)
            {
                result.Append(".*");
                break;
            }

            var fixedPart = new StringBuilder();
            while (i < n && !ReferenceEquals(parts[i], star))
            {
                fixedPart.Append(parts[i]);
                i++;
            }

            if (i == n)
            {
                result.Append(".*").Append(fixedPart);
            }
            else
            {
                result.Append("(?>.*?").Append(fixedPart).Append(')');
            }
        }

        return "(?s:" + result + ")\\Z";
    }

    // ntpath.normcase: LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE) over the text with "/" replaced by "\".
    private static string WindowsNormCase(string text) => text.Replace('/', '\\').ToLowerInvariant();
}
