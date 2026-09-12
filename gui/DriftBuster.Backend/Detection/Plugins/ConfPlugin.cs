using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects notable .conf DSLs not covered by INI heuristics: Elastic Logstash pipelines with <c>input { }</c>,
/// <c>filter { }</c> and <c>output { }</c> blocks holding nested plugin stanzas. Detection stays tight so INI-style
/// .conf files (Splunk and friends) keep going to the INI plugin.
/// </summary>
/// <remarks>
/// The block pattern is the Python regex <c>^\s*(input|filter|output)\s*\{</c> (MULTILINE) with <c>\s</c> spelled
/// <c>[\s\x1c-\x1f]</c>, <c>^</c> spelled <c>\G</c> and driven from every line start by <see cref="LineStartMatcher"/>
/// (linear over blank-line runs); every other construct is ASCII, so match positions, captures and the
/// non-overlapping count are identical to <c>finditer</c>. The nested-stanza
/// pattern <c>^\s*[a-zA-Z_][\w-]*\s*\{</c> uses <c>\w</c>, which .NET defines as [L Mn Nd Pc] on UTF-16 units where
/// Python uses [L N _] on code points, so it is matched by hand.
/// </remarks>
public sealed class ConfPlugin : IFormatPlugin
{
    private const string PythonSpace = @"[\s\x1c-\x1f]";

    internal static readonly Regex LogstashBlockPattern = new(
        @"\G" + PythonSpace + "*(?<block>input|filter|output)" + PythonSpace + @"*\{",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    public string Name => "conf";

    public int Priority => 150;

    public string Version => "0.0.1";

    private static int SkipSpaces(string text, int offset)
    {
        while (offset < text.Length && PythonText.IsSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }

    // [a-zA-Z_][\w-]* from offset: returns the end of the token, or -1 when the first character does not qualify.
    private static int ScanStanzaName(string text, int offset)
    {
        if (offset >= text.Length || !(char.IsAsciiLetter(text[offset]) || text[offset] == '_'))
        {
            return -1;
        }

        offset++;
        while (offset < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed);
            if (rune.Value == '-' || PythonText.IsWordRune(rune))
            {
                offset += consumed;
                continue;
            }

            break;
        }

        return offset;
    }

    // ^\s*[a-zA-Z_][\w-]*\s*\{ (MULTILINE) searched over the text: at the start and after every "\n", skip whitespace
    // (which may cross further newlines), take the stanza name, skip whitespace again and require "{". The greedy
    // name never needs to backtrack because the character after it is neither whitespace nor "{". Each whitespace
    // run is skipped once: every line start inside it reaches the same offset (see LineStartMatcher).
    internal static bool HasNestedStanza(string text)
    {
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var nameStart = SkipSpaces(text, lineStart);
            var nameEnd = ScanStanzaName(text, nameStart);
            if (nameEnd >= 0)
            {
                var brace = SkipSpaces(text, nameEnd);
                if (brace < text.Length && text[brace] == '{')
                {
                    return true;
                }
            }

            lineStart = LineStartMatcher.NextLineStart(text, nameStart + 1);
        }

        return false;
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var blocks = LineStartMatcher.Matches(LogstashBlockPattern, text).Select(match => match.Groups["block"].Value).ToList();
        if (blocks.Count < 1)
        {
            return null;
        }

        var reasons = new List<string>
        {
            "Detected Logstash pipeline block(s): " + string.Join(", ", blocks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
        };

        // At least one nested plugin stanza strengthens the signal.
        if (HasNestedStanza(text))
        {
            reasons.Add("Found nested plugin stanza inside pipeline block");
        }

        return new DetectionMatch(Name, "unix-conf", "logstash-pipeline", blocks.Count >= 2 ? 0.8 : 0.72, reasons, metadata: null);
    }
}
