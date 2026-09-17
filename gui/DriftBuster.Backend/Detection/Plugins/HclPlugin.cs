using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Heuristic HCL detector for HashiCorp configs (Nomad, Vault, Consul): the <c>.hcl</c> extension, block forms such
/// as <c>job {}</c>, <c>server {}</c>, <c>listener {}</c> and <c>seal {}</c>, and <c>key = value</c> assignments.
/// </summary>
/// <remarks>
/// The block pattern <c>^\s*(job|server|seal|listener|datacenter|client)\b[^\n{]*\{</c> (MULTILINE) is matched by
/// hand because Python's <c>\b</c> derives from <c>\w</c> = [L N _] on code points and .NET's from [L Mn Nd Pc] on
/// UTF-16 units. The assignment pattern <c>^\s*[A-Za-z0-9_.\-]+\s*=\s*\S+</c> (MULTILINE) is the regex with
/// <c>\s</c> spelled <c>[\s\x1c-\x1f]</c>, <c>\S</c> as its complement and <c>^</c> spelled <c>\G</c>, driven from
/// every line start by <see cref="LineStartMatcher"/> (linear over blank-line runs); every other construct is
/// ASCII, and only match existence is consumed.
/// </remarks>
public sealed class HclPlugin : IFormatPlugin
{
    private const string Extension = ".hcl";
    private const int BlocksPreviewLimit = 5;
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    private static readonly string[] BlockKeywords = ["job", "server", "seal", "listener", "datacenter", "client"];

    internal static readonly Regex KeyValuePattern = new(
        @"\G" + EngineSpace + @"*[A-Za-z0-9_.\-]+" + EngineSpace + "*=" + EngineSpace + @"*[^\s\x1c-\x1f]+",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    public string Name => "hcl";

    public int Priority => 158;

    public string Version => "0.0.1";

    private static int SkipSpaces(string text, int offset)
    {
        while (offset < text.Length && EngineText.IsSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }

    // Python \b after a word character: the next code point is not [L N _], or the text ends.
    private static bool AtWordEnd(string text, int offset)
    {
        if (offset >= text.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out _);
        return !EngineText.IsWordRune(rune);
    }

    // "[^\n{]*\{" from offset: the first "{" before the next "\n" ends the match; returns -1 when there is none.
    private static int FindOpeningBrace(string text, int offset)
    {
        while (offset < text.Length && text[offset] != '\n')
        {
            if (text[offset] == '{')
            {
                return offset;
            }

            offset++;
        }

        return -1;
    }

    // Tries the alternation at offset. The keywords are not prefixes of one another, so at most one can match; the
    // group value is the literal keyword as the pattern has no IGNORECASE.
    private static (string? Keyword, int End) MatchBlockAt(string text, int offset)
    {
        foreach (var keyword in BlockKeywords)
        {
            if (!text.AsSpan(offset).StartsWith(keyword, StringComparison.Ordinal) || !AtWordEnd(text, offset + keyword.Length))
            {
                continue;
            }

            var brace = FindOpeningBrace(text, offset + keyword.Length);
            return brace < 0 ? (null, -1) : (keyword, brace + 1);
        }

        return (null, -1);
    }

    // re.findall semantics: leftmost non-overlapping matches, each anchored at the start of the text or after a "\n"
    // with "\s*" free to cross further newlines. The search resumes at the first line start past a match, and a
    // failed whitespace run is skipped once: every line start inside it reaches the same offset (see LineStartMatcher).
    internal static List<string> FindBlocks(string text)
    {
        var blocks = new List<string>();
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var keywordStart = SkipSpaces(text, lineStart);
            var (keyword, end) = MatchBlockAt(text, keywordStart);
            if (keyword is not null)
            {
                blocks.Add(keyword);
                lineStart = LineStartMatcher.NextLineStart(text, end);
                continue;
            }

            lineStart = LineStartMatcher.NextLineStart(text, keywordStart + 1);
        }

        return blocks;
    }

    private static string ChooseVariant(List<string> blocks)
    {
        var lowered = blocks.Select(EngineText.Lower).ToList();
        if (lowered.Contains("job", StringComparer.Ordinal))
        {
            return "hashicorp-nomad";
        }

        if (lowered.Any(block => block is "seal" or "listener"))
        {
            return "hashicorp-vault";
        }

        if (lowered.Contains("server", StringComparer.Ordinal) || lowered.Contains("datacenter", StringComparer.Ordinal))
        {
            return "hashicorp-consul";
        }

        return "generic";
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var hasExtension = string.Equals(PathText.SuffixLower(path), Extension, StringComparison.Ordinal);
        var reasons = new List<string>();
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);

        if (hasExtension)
        {
            reasons.Add("File extension .hcl suggests HashiCorp HCL");
        }

        var blocks = FindBlocks(text);
        var hasKeyValues = LineStartMatcher.IsMatch(KeyValuePattern, text);
        if (blocks.Count > 0)
        {
            reasons.Add("Found HCL-style block declarations (e.g., job/server)");
            metadata["blocks_preview"] = blocks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(BlocksPreviewLimit).ToList();
        }

        if (hasKeyValues)
        {
            reasons.Add("Detected key = value assignments");
        }

        var signals = new[] { hasExtension, blocks.Count > 0, hasKeyValues }.Count(flag => flag);
        if (signals < 2)
        {
            return null;
        }

        var variant = ChooseVariant(blocks);
        var confidence = 0.55;
        if (hasExtension)
        {
            confidence += 0.2;
        }

        if (blocks.Count > 0)
        {
            confidence += 0.15;
        }

        if (hasKeyValues)
        {
            confidence += 0.05;
        }

        confidence = Math.Min(0.95, confidence);

        if (reasons.Count == 0)
        {
            reasons.Add("HCL structure detected");
        }

        return new DetectionMatch(Name, "hcl", variant, confidence, reasons, metadata.Count > 0 ? metadata : null);
    }
}
