using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Linear-time driver for the Python <c>^\s*...</c> MULTILINE patterns. A .NET regex searched with <c>^</c> under
/// <see cref="RegexOptions.Multiline"/> re-scans and backtracks a run of blank lines from every line start inside
/// it (quadratic, and past the match timeout well inside one sample), so the plugins spell those patterns with
/// <c>\G</c> and drive them from here.
/// </summary>
public static class LineStartMatcher
{
    /// <summary>
    /// The matches Python's <c>finditer</c> yields for a <c>^\s*&lt;anchor&gt;...</c> MULTILINE pattern, given its
    /// <c>\G</c>-anchored spelling. <paramref name="pattern"/> must begin with <c>\G[\s\x1c-\x1f]*</c> followed by
    /// something no whitespace code point can start, so a match from a line start is decided entirely at the end of
    /// the whitespace run that begins there: every line start inside a run that failed reaches the same end and
    /// fails the same way, and every one inside a run that matched lies within that match. Each code point is
    /// therefore visited a bounded number of times.
    /// </summary>
    public static IReadOnlyList<Match> Matches(Regex pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<Match>();
        var position = 0;
        while (position <= text.Length)
        {
            var match = pattern.Match(text, position);
            if (match.Success)
            {
                matches.Add(match);
                position = NextLineStart(text, match.Index + match.Length);
                continue;
            }

            position = NextLineStart(text, SkipSpaces(text, position) + 1);
        }

        return matches;
    }

    /// <summary>Python <c>search</c> truthiness for the same pattern shape: stops at the first match.</summary>
    public static bool IsMatch(Regex pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);
        var position = 0;
        while (position <= text.Length)
        {
            if (pattern.IsMatch(text, position))
            {
                return true;
            }

            position = NextLineStart(text, SkipSpaces(text, position) + 1);
        }

        return false;
    }

    /// <summary>The end of the Python <c>\s*</c> run starting at <paramref name="offset"/>.</summary>
    public static int SkipSpaces(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        while (offset < text.Length && PythonText.IsSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }

    /// <summary>
    /// The first MULTILINE <c>^</c> position at or after <paramref name="offset"/>: the offset itself when it is 0
    /// or follows a <c>\n</c>, else just past the next <c>\n</c>; <c>text.Length + 1</c> when there is none, which
    /// ends a <c>while (position &lt;= text.Length)</c> loop. Python's MULTILINE <c>^</c> matches after <c>\n</c>
    /// only. Hand-rolled scanners that tried a line start, skipped its whitespace run to <c>end</c> and failed call
    /// <c>NextLineStart(text, end + 1)</c>: every line start inside the run fails identically, and <c>text[end]</c>
    /// is not whitespace, so the next line start lies strictly beyond it.
    /// </summary>
    public static int NextLineStart(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset == 0 || (offset <= text.Length && text[offset - 1] == '\n'))
        {
            return offset;
        }

        if (offset >= text.Length)
        {
            return text.Length + 1;
        }

        var newline = text.IndexOf('\n', offset);
        return newline < 0 ? text.Length + 1 : newline + 1;
    }
}
