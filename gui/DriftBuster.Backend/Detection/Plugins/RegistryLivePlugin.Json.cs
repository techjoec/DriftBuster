using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>The JSON key probe, matched by hand so a long line of braces cannot make it quadratic.</summary>
public sealed partial class RegistryLivePlugin
{
    private const string JsonKeyLiteral = "\"registry_scan\"";

    /// <summary>
    /// Python <c>re.search(r'\{[^\n\r]*"registry_scan"\s*:\s*\{', text)</c> (DOTALL changes nothing: the pattern
    /// has no dot). A match exists exactly when some <c>"registry_scan"</c> occurrence has a <c>{</c> earlier on
    /// its line (no <c>\n</c> or <c>\r</c> between them) and is followed by optional whitespace, a colon,
    /// optional whitespace and a <c>{</c>, where the whitespace may cross line breaks. One pass tracks whether a
    /// brace has been seen since the last <c>\n</c> or <c>\r</c>; the regex would rescan the line from every
    /// brace.
    /// </summary>
    internal static bool HasJsonKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var braceOnLine = false;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c is '\n' or '\r')
            {
                braceOnLine = false;
            }
            else if (c == '{')
            {
                braceOnLine = true;
            }
            else if (c == '"' && braceOnLine && text.AsSpan(index).StartsWith(JsonKeyLiteral, StringComparison.Ordinal)
                && FollowedByColonAndBrace(text, index + JsonKeyLiteral.Length))
            {
                return true;
            }
        }

        return false;
    }

    // \s*:\s*\{ from offset.
    private static bool FollowedByColonAndBrace(string text, int offset)
    {
        var position = LineStartMatcher.SkipSpaces(text, offset);
        if (position >= text.Length || text[position] != ':')
        {
            return false;
        }

        position = LineStartMatcher.SkipSpaces(text, position + 1);
        return position < text.Length && text[position] == '{';
    }
}
