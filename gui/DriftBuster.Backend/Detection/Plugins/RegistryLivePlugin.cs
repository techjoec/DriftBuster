using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects registry live-scan definition manifests: a JSON document whose top-level <c>registry_scan</c> object
/// carries a token, keyword and pattern lists and the depth/hit/time budgets, or a YAML file with a
/// <c>registry_scan:</c> key read by line heuristics without a YAML parser.
/// </summary>
/// <remarks>
/// Every Python <c>^\s*...</c> MULTILINE pattern is spelled with <c>\G</c> and driven from each line start by
/// <see cref="LineStartMatcher"/>; <c>\s</c> becomes <c>[\s\x1c-\x1f]</c>, <c>.</c> without DOTALL becomes
/// <c>[^\n]</c>, and <c>$</c> under <see cref="RegexOptions.Multiline"/> matches before every <c>\n</c> and at the
/// end on both sides. IGNORECASE on <c>registry_scan</c> is spelled out per letter: a str-pattern letter matches
/// every code point whose simple lower case is that letter, which for these letters adds U+0130 and U+0131 to
/// <c>i</c> and U+017F to <c>s</c> and nothing to the others.
/// </remarks>
public sealed partial class RegistryLivePlugin : IFormatPlugin
{
    private const string PythonSpace = @"[\s\x1c-\x1f]";

    // ^\s*registry_scan\s*:\s*$ (IGNORECASE | MULTILINE)
    internal static readonly Regex YamlKeyPattern = new(
        @"\G" + PythonSpace + "*[rR][eE][gG][iIİı][sSſ][tT][rR][yY]_[sSſ][cC][aA][nN]" + PythonSpace + "*:" + PythonSpace + "*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*token\s*:\s*(?P<val>.+)$ (MULTILINE)
    internal static readonly Regex YamlTokenPattern = new(
        @"\G" + PythonSpace + "*token" + PythonSpace + "*:" + PythonSpace + @"*(?<val>[^\n]+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*keywords\s*:\s*\[.+\]$ (MULTILINE)
    internal static readonly Regex YamlKeywordsPattern = new(
        @"\G" + PythonSpace + "*keywords" + PythonSpace + "*:" + PythonSpace + @"*\[[^\n]+\]$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ^\s*patterns\s*:\s*(\[|-)\s* (MULTILINE)
    internal static readonly Regex YamlPatternsPattern = new(
        @"\G" + PythonSpace + "*patterns" + PythonSpace + "*:" + PythonSpace + @"*(?:\[|-)" + PythonSpace + "*",
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

        // Quick filename/extension hints ("registry" contains "reg", so the Python tuple reduces to reg or scan).
        if (lower.EndsWith(".regscan.json", StringComparison.Ordinal) || lower.EndsWith(".registry.json", StringComparison.Ordinal))
        {
            reasons.Add("Filename suggests a registry scan JSON manifest");
        }
        else if (extension is ".json" or ".yml" or ".yaml"
            && (lower.Contains("reg", StringComparison.Ordinal) || lower.Contains("scan", StringComparison.Ordinal)))
        {
            reasons.Add("Filename contains registry/scan hints");
        }

        // Prefer JSON detection
        object? parsedJson = null;
        if (extension is ".json" or "" || HasJsonKey(text))
        {
            if (HasJsonKey(text))
            {
                reasons.Add("Found 'registry_scan' top-level key in JSON payload");
            }

            if (!PythonJson.TryLoads(text, out parsedJson))
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

        // YAML heuristic (no strict parsing to avoid dependency)
        if (extension is ".yml" or ".yaml" && LineStartMatcher.IsMatch(YamlKeyPattern, text))
        {
            return BuildYamlMatch(text, reasons, metadata);
        }

        return null;
    }

    private DetectionMatch BuildJsonMatch(OrderedDictionary<string, object?> spec, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        if (spec.TryGetValue("token", out var token) && token is string tokenText && PythonText.Strip(tokenText).Length > 0)
        {
            metadata["token"] = PythonText.Strip(tokenText);
            reasons.Add($"Token provided: {PythonText.Strip(tokenText)}");
        }

        if (spec.TryGetValue("keywords", out var keywords) && keywords is List<object?> keywordItems)
        {
            var kw = keywordItems.Select(PythonRepr.Str).Where(item => PythonText.Strip(item).Length > 0).ToList();
            if (kw.Count > 0)
            {
                metadata["keywords"] = kw;
                reasons.Add("Keyword list provided");
            }
        }

        if (spec.TryGetValue("patterns", out var patterns) && patterns is List<object?> patternItems)
        {
            var pt = patternItems.Select(PythonRepr.Str).Where(item => PythonText.Strip(item).Length > 0).ToList();
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

        // Python sums 0.65 + 0.05 * flag + ... left to right; the same operations give the same double.
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
            var token = PythonText.Strip(tokenMatch.Groups["val"].Value).Trim('"', '\'');
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
