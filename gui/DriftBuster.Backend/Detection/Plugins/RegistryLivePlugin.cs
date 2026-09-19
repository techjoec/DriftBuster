using System.Text.Json;
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

        if (extension is ".json" or "" || HasJsonKey(text))
        {
            if (HasJsonKey(text))
            {
                reasons.Add("Found 'registry_scan' top-level key in JSON payload");
            }

            using var document = ScannedJson.TryParse(text, ScannedJson.Lenient);
            if (document is not null
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("registry_scan", out var spec)
                && spec.ValueKind == JsonValueKind.Object)
            {
                return BuildJsonMatch(spec, reasons, metadata);
            }
        }

        // YAML by heuristics only; no YAML parser here.
        if (extension is ".yml" or ".yaml" && LineStartMatcher.IsMatch(YamlKeyPattern, text))
        {
            return BuildYamlMatch(text, reasons, metadata);
        }

        return null;
    }

    private DetectionMatch BuildJsonMatch(JsonElement spec, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        if (spec.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString()!.Trim().Length > 0)
        {
            metadata["token"] = token.GetString()!.Trim();
            reasons.Add($"Token provided: {metadata["token"]}");
        }

        if (NonBlankItems(spec, "keywords") is { Count: > 0 } keywords)
        {
            metadata["keywords"] = keywords;
            reasons.Add("Keyword list provided");
        }

        if (NonBlankItems(spec, "patterns") is { Count: > 0 } patterns)
        {
            metadata["patterns"] = patterns;
            reasons.Add("Pattern list provided");
        }

        foreach (var option in PassThroughOptions)
        {
            if (spec.TryGetProperty(option, out var value))
            {
                metadata[option] = OptionValue(value);
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

    // Numbers stay numbers (integral as long); anything else as its text.
    private static object OptionValue(JsonElement value) => value.ValueKind != JsonValueKind.Number
        ? ScannedJson.ScalarText(value)
        : value.TryGetInt64(out var integral) ? integral : value.GetDouble();

    // The array's items as text, blanks dropped; null when the property is missing or not an array.
    private static List<string>? NonBlankItems(JsonElement spec, string name)
        => spec.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(ScannedJson.ScalarText).Where(item => item.Trim().Length > 0).ToList()
            : null;

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
