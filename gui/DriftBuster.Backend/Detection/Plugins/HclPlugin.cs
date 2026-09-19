using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// HCL detector for HashiCorp configs (Nomad, Vault, Consul): <c>.hcl</c> extension, blocks such as <c>job {}</c>,
/// <c>server {}</c>, <c>listener {}</c>, <c>seal {}</c>, and <c>key = value</c> assignments.
/// </summary>
/// <remarks>
/// The block pattern <c>^\s*(job|server|seal|listener|datacenter|client)\b[^\n{]*\{</c> and the assignment pattern run as
/// <c>\G</c> regexes through <see cref="LineStartMatcher"/>.
/// </remarks>
public sealed partial class HclPlugin : IFormatPlugin
{
    private const string Extension = ".hcl";
    private const int BlocksPreviewLimit = 5;

    [GeneratedRegex(@"\G\s*(?<keyword>job|server|seal|listener|datacenter|client)\b[^\n{]*\{", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 2000)]
    internal static partial Regex BlockPattern { get; }

    [GeneratedRegex(@"\G\s*[A-Za-z0-9_.\-]+\s*=\s*\S+", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 2000)]
    internal static partial Regex KeyValuePattern { get; }

    public string Name => "hcl";

    public int Priority => 158;

    public string Version => "0.0.1";

    // Leftmost non-overlapping block matches from line starts.
    internal static List<string> FindBlocks(string text)
        => LineStartMatcher.Matches(BlockPattern, text).Select(match => match.Groups["keyword"].Value).ToList();

    private static string ChooseVariant(List<string> blocks)
    {
        var lowered = blocks.Select(item => item.ToLowerInvariant()).ToList();
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
        var metadata = new JsonObject();

        if (hasExtension)
        {
            reasons.Add("File extension .hcl suggests HashiCorp HCL");
        }

        var blocks = FindBlocks(text);
        var hasKeyValues = LineStartMatcher.IsMatch(KeyValuePattern, text);
        if (blocks.Count > 0)
        {
            reasons.Add("Found HCL-style block declarations (e.g., job/server)");
            metadata["blocks_preview"] = JsonNodes.Strings(blocks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(BlocksPreviewLimit).ToList());
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

        return new DetectionMatch(Name, "hcl", variant, confidence, reasons, metadata);
    }
}
