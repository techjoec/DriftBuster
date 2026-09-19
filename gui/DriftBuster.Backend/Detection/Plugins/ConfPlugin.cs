using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Logstash pipelines (<c>input</c>/<c>filter</c>/<c>output</c> blocks with nested plugin stanzas). Kept tight so
/// INI-style .conf files stay with the INI plugin.
/// </summary>
/// <remarks>
/// The block and stanza patterns (<c>^\s*(input|filter|output)\s*\{</c>, <c>^\s*[a-zA-Z_][\w-]*\s*\{</c>) run as <c>\G</c>
/// regexes through <see cref="LineStartMatcher"/>.
/// </remarks>
public sealed partial class ConfPlugin : IFormatPlugin
{
    [GeneratedRegex(@"\G\s*(?<block>input|filter|output)\s*\{", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 2000)]
    internal static partial Regex LogstashBlockPattern { get; }

    [GeneratedRegex(@"\G\s*[a-zA-Z_][\w-]*\s*\{", RegexOptions.Multiline | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex NestedStanzaPattern { get; }

    public string Name => "conf";

    public int Priority => 150;

    public string Version => "0.0.1";

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
        if (LineStartMatcher.IsMatch(NestedStanzaPattern, text))
        {
            reasons.Add("Found nested plugin stanza inside pipeline block");
        }

        return new DetectionMatch(Name, "unix-conf", "logstash-pipeline", blocks.Count >= 2 ? 0.8 : 0.72, reasons, metadata: null);
    }
}
