using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Linear-time driver for <c>^\s*...</c> multiline patterns: a .NET regex with <c>^</c> under <see cref="RegexOptions.Multiline"/>
/// rescans a run of blank lines from every line start (quadratic, hitting the match timeout), so plugins write these patterns
/// with <c>\G</c> and drive them from here.
/// </summary>
public static class LineStartMatcher
{
    /// <summary>
    /// Non-overlapping matches of a <c>\G\s*...</c> pattern tried at each line start. After the whitespace the pattern
    /// must need a non-whitespace character, so every line start inside one whitespace run succeeds or fails the same way and each
    /// character is visited a bounded number of times.
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

    /// <summary>Whether <see cref="Matches"/> would find anything; stops at the first match.</summary>
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

    /// <summary>End of the whitespace run (<see cref="char.IsWhiteSpace(char)"/>) starting at <paramref name="offset"/>.</summary>
    public static int SkipSpaces(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }

    /// <summary>
    /// The first line start at or after <paramref name="offset"/> (only <c>\n</c> starts a line); <c>text.Length + 1</c> when none.
    /// Scanners that failed at a line start after skipping whitespace to <c>end</c> call <c>NextLineStart(text, end + 1)</c>.
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
