using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects registry live-scan definitions: JSON with a top-level <c>registry_scan</c> object (token, keywords, patterns,
/// budgets), or YAML with a <c>registry_scan:</c> key read by line heuristics.
/// </summary>
/// <remarks>
/// The <c>^\s*</c> multiline patterns use <c>\G</c> through <see cref="LineStartMatcher"/>; case-insensitive
/// <c>registry_scan</c> also accepts U+0130/U+0131 for i and U+017F for s.
/// </remarks>
public sealed partial class RegistryLivePlugin : IFormatPlugin
{
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    // ^\s*registry_scan\s*:\s*$, case-insensitive.
    internal static readonly Regex YamlKeyPattern = new(
        @"\G" + EngineSpace + "*[rR][eE][gG][iIİı][sSſ][tT][rR][yY]_[sSſ][cC][aA][nN]" + EngineSpace + "*:" + EngineSpace + "*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*token\s*:\s*(.+)$
    internal static readonly Regex YamlTokenPattern = new(
        @"\G" + EngineSpace + "*token" + EngineSpace + "*:" + EngineSpace + @"*(?<val>[^\n]+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*keywords\s*:\s*\[.+\]$
    internal static readonly Regex YamlKeywordsPattern = new(
        @"\G" + EngineSpace + "*keywords" + EngineSpace + "*:" + EngineSpace + @"*\[[^\n]+\]$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*patterns\s*:\s*(\[|-)\s*
    internal static readonly Regex YamlPatternsPattern = new(
        @"\G" + EngineSpace + "*patterns" + EngineSpace + "*:" + EngineSpace + @"*(?:\[|-)" + EngineSpace + "*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly string[] PassThroughOptions = ["max_depth", "max_hits", "time_budget_s"];

    public string Name => "registry-live";

    public int Priority => 30;

    public string Version => "0.0.1";

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var lower = PathText.NameLower(path);
        var extension = PathText.SuffixLower(path);
        var reasons = new List<string>();
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);

        // Quick filename/extension hints ("registry" contains "reg", so the hint list reduces to reg or scan).
        if (lower.EndsWith(".regscan.json", StringComparison.Ordinal) || lower.EndsWith(".registry.json", StringComparison.Ordinal))
        {
            reasons.Add("Filename suggests a registry scan JSON manifest");
        }
        else if (extension is ".json" or ".yml" or ".yaml"
            && (lower.Contains("reg", StringComparison.Ordinal) || lower.Contains("scan", StringComparison.Ordinal)))
        {
            reasons.Add("Filename contains registry/scan hints");
        }

        object? parsedJson = null;
        if (extension is ".json" or "" || HasJsonKey(text))
        {
            if (HasJsonKey(text))
            {
                reasons.Add("Found 'registry_scan' top-level key in JSON payload");
            }

            if (!EngineJson.TryLoads(text, out parsedJson))
            {
                parsedJson = null;
            }
        }

        if (parsedJson is OrderedDictionary<string, object?> { Count: > 0 } document
            && document.TryGetValue("registry_scan", out var spec)
            && spec is OrderedDictionary<string, object?> specification)
        {
            return BuildJsonMatch(specification, reasons, metadata);
        }

        // YAML by heuristics only; no YAML parser here.
        if (extension is ".yml" or ".yaml" && LineStartMatcher.IsMatch(YamlKeyPattern, text))
        {
            return BuildYamlMatch(text, reasons, metadata);
        }

        return null;
    }

    private DetectionMatch BuildJsonMatch(OrderedDictionary<string, object?> spec, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        if (spec.TryGetValue("token", out var token) && token is string tokenText && EngineText.Strip(tokenText).Length > 0)
        {
            metadata["token"] = EngineText.Strip(tokenText);
            reasons.Add($"Token provided: {EngineText.Strip(tokenText)}");
        }

        if (spec.TryGetValue("keywords", out var keywords) && keywords is List<object?> keywordItems)
        {
            var kw = keywordItems.Select(EngineRepr.Str).Where(item => EngineText.Strip(item).Length > 0).ToList();
            if (kw.Count > 0)
            {
                metadata["keywords"] = kw;
                reasons.Add("Keyword list provided");
            }
        }

        if (spec.TryGetValue("patterns", out var patterns) && patterns is List<object?> patternItems)
        {
            var pt = patternItems.Select(EngineRepr.Str).Where(item => EngineText.Strip(item).Length > 0).ToList();
            if (pt.Count > 0)
            {
                metadata["patterns"] = pt;
                reasons.Add("Pattern list provided");
            }
        }

        foreach (var option in PassThroughOptions)
        {
            if (spec.TryGetValue(option, out var value))
            {
                metadata[option] = value;
            }
        }

        // Summed left to right; the order fixes the resulting double.
        var confidence = 0.65
            + (0.05 * (metadata.ContainsKey("token") ? 1 : 0))
            + (0.05 * (metadata.ContainsKey("keywords") ? 1 : 0))
            + (0.05 * (metadata.ContainsKey("patterns") ? 1 : 0));
        confidence = Math.Min(0.9, confidence);

        return new DetectionMatch(
            Name,
            "registry-live",
            "scan-definition",
            confidence,
            reasons.Count > 0 ? reasons : ["JSON manifest indicates registry live scan"],
            metadata.Count > 0 ? metadata : null);
    }

    private DetectionMatch BuildYamlMatch(string text, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        reasons.Add("Detected 'registry_scan:' key in YAML content");

        // Best-effort extraction for a few keys
        var tokenMatch = LineStartMatcher.Matches(YamlTokenPattern, text).FirstOrDefault();
        if (tokenMatch is not null)
        {
            var token = EngineText.Strip(tokenMatch.Groups["val"].Value).Trim('"', '\'');
            if (token.Length > 0)
            {
                metadata["token"] = token;
            }
        }

        if (LineStartMatcher.IsMatch(YamlKeywordsPattern, text))
        {
            reasons.Add("Inline keywords list present");
        }

        if (LineStartMatcher.IsMatch(YamlPatternsPattern, text))
        {
            reasons.Add("Pattern list present");
        }

        if (metadata.TryGetValue("token", out var provided))
        {
            reasons.Add($"Token provided: {provided}");
        }

        return new DetectionMatch(
            Name,
            "registry-live",
            "scan-definition",
            metadata.ContainsKey("token") ? 0.7 : 0.62,
            reasons,
            metadata.Count > 0 ? metadata : null);
    }
}
