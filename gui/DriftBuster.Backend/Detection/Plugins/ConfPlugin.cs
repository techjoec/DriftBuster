using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Logstash pipelines (<c>input</c>/<c>filter</c>/<c>output</c> blocks with nested plugin stanzas). Kept tight so
/// INI-style .conf files stay with the INI plugin.
/// </summary>
/// <remarks>
/// The block pattern <c>^\s*(input|filter|output)\s*\{</c> runs as a <c>\G</c> regex through <see cref="LineStartMatcher"/>;
/// the stanza pattern <c>^\s*[a-zA-Z_][\w-]*\s*\{</c> is matched by hand because .NET's <c>\w</c> differs.
/// </remarks>
public sealed class ConfPlugin : IFormatPlugin
{
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    internal static readonly Regex LogstashBlockPattern = new(
        @"\G" + EngineSpace + "*(?<block>input|filter|output)" + EngineSpace + @"*\{",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    public string Name => "conf";

    public int Priority => 150;

    public string Version => "0.0.1";

    private static int SkipSpaces(string text, int offset)
    {
        while (offset < text.Length && EngineText.IsSpace(text[offset]))
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
            if (rune.Value == '-' || EngineText.IsWordRune(rune))
            {
                offset += consumed;
                continue;
            }

            break;
        }

        return offset;
    }

    // ^\s*[a-zA-Z_][\w-]*\s*\{ over the text; whitespace runs are skipped once as in LineStartMatcher.
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
