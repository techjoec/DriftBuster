using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// The YAML patterns. The <c>^\s*</c> multiline ones run as <c>\G</c> regexes through <see cref="LineStartMatcher"/>; whitespace
/// runs may cross line breaks, as the patterns allow.
/// </summary>
public sealed partial class YamlPlugin
{
    // ^\s*[A-Za-z_][\w.-]*\s*:\s*(\S|$): the tail swallows the first non-space character after the colon, even on a later line,
    // so a key on that line is not counted.
    [GeneratedRegex(@"\G\s*(?<key>[A-Za-z_][\w.-]*)\s*:\s*(?:\S|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 2000)]
    internal static partial Regex KeyColonPattern { get; }

    [GeneratedRegex(@"\G\s*---\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex DocumentStartPattern { get; }

    [GeneratedRegex(@"\G\s*\.\.\.\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex DocumentEndPattern { get; }

    // "\s+" may cross line breaks, so "-" alone on a line followed by text matches.
    [GeneratedRegex(@"\G\s*-\s+\S", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex ListMarkerPattern { get; }

    // The trailing "\s*" swallows following line breaks, so a commented key right after another is not counted.
    [GeneratedRegex(@"\G\s*#\s*[A-Za-z_][\w.-]*\s*:\s*", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex CommentedKeyPattern { get; }

    // At least two whitespace characters (line breaks included) after a "\n", then a key. Not line-anchored, so the
    // non-backtracking engine keeps it linear over long whitespace runs.
    private static readonly Regex IndentedBlockPattern = new(
        @"\n\s{2,}[A-Za-z_][\w.-]*\s*:",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    private static readonly ConcurrentDictionary<string, Regex> KeyWithValuePatterns = new(StringComparer.Ordinal);

    private static List<string> KeyColonMatches(string s)
        => LineStartMatcher.Matches(KeyColonPattern, s).Select(match => match.Groups["key"].Value).ToList();

    private static bool HasListMarker(string s) => LineStartMatcher.IsMatch(ListMarkerPattern, s);

    private static bool HasIndentedBlock(string s) => IndentedBlockPattern.IsMatch(s);

    private static int CountCommentedKeys(string s) => LineStartMatcher.Matches(CommentedKeyPattern, s).Count;

    // ^\s*<literal>\s*:\s*\S+ (multiline), case-sensitive.
    private static bool HasKeyWithValue(string s, string literal)
    {
        var pattern = KeyWithValuePatterns.GetOrAdd(
            literal,
            key => new Regex(@"\G\s*" + Regex.Escape(key) + @"\s*:\s*\S", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));
        return LineStartMatcher.IsMatch(pattern, s);
    }
}
