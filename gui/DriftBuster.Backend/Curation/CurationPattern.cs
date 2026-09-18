using System.IO.Enumeration;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// Wildcard matching for curation patterns: <c>*</c> matches any run of characters (a <c>/</c> included), <c>?</c> one
/// character, always case-insensitive because files are matched across servers case-insensitively. An empty pattern matches
/// everything; a pattern without wildcards must equal the text.
/// </summary>
public static class CurationPattern
{
    public static bool IsMatch(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0)
        {
            return true;
        }

        // MatchesSimpleExpression reads \ as an escape; curation patterns have none, so each one stands for itself.
        return FileSystemName.MatchesSimpleExpression(pattern.Replace("\\", "\\\\", StringComparison.Ordinal), text, ignoreCase: true);
    }
}
