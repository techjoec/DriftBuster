using System.IO.Enumeration;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The one wildcard syntax DriftBuster uses for path patterns: <c>*</c> matches any run of characters (a <c>/</c> included, so
/// <c>configs/*.json</c> matches <c>configs/sub/app.json</c>) and <c>?</c> matches exactly one character. <c>[</c> and <c>]</c> are
/// ordinary characters. Matching is case-insensitive on Windows and case-sensitive elsewhere.
/// </summary>
/// <remarks>
/// Built on <see cref="FileSystemName.MatchesSimpleExpression(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)"/>. On Windows every
/// <c>\</c> in the pattern and the text is read as <c>/</c> first; elsewhere a <c>\</c> is an ordinary character, so there is no
/// escape and no way to match a <c>*</c> or <c>?</c> literally. An empty pattern never matches, and no pattern matches empty text.
/// The offline runner's <c>Test-DbWildcardMatch</c> implements the same syntax.
/// </remarks>
public static class PathWildcard
{
    /// <summary>True when <paramref name="text"/> holds a wildcard character (<c>*</c> or <c>?</c>).</summary>
    public static bool HasWildcards(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.AsSpan().IndexOfAny('*', '?') >= 0;
    }

    /// <summary>True when the whole of <paramref name="text"/> matches <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0 || text.Length == 0)
        {
            return false;
        }

        var windows = OperatingSystem.IsWindows();
        if (windows)
        {
            text = text.Replace('\\', '/');
            pattern = pattern.Replace('\\', '/');
        }
        else
        {
            // MatchesSimpleExpression reads \ as an escape; the shared syntax has no escape, so each one is doubled to stand
            // for itself.
            pattern = pattern.Replace("\\", "\\\\", StringComparison.Ordinal);
        }

        return FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: windows);
    }
}
