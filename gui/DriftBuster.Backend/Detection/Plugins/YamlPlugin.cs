using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// YAML heuristics: top-level and nested <c>key: value</c> pairs, document and list markers, filename hints. Variant
/// <c>kubernetes-manifest</c> when both <c>apiVersion:</c> and <c>kind:</c> keys are present, else <c>generic</c>.
/// </summary>
/// <remarks>
/// The patterns are multiline with <c>\s*</c> runs that may cross line breaks and <c>(\S|$)</c> tails that consume the next
/// line's first character, so a key right after a valueless key is skipped. They are matched by hand on code points in
/// <c>YamlPlugin.Scan.cs</c> to keep these rules exact.
/// </remarks>
public sealed partial class YamlPlugin : IFormatPlugin
{
    private const int TopLevelKeyPreviewLimit = 8;
    private const int CommentedKeyThreshold = 6;
    private const string TabReviewReason = "Tab indentation present in YAML-like content";

    private static readonly HashSet<string> YamlExtensions = new(StringComparer.Ordinal) { ".yml", ".yaml" };
    private static readonly HashSet<string> IniLikeExtensions = new(StringComparer.Ordinal)
    {
        ".conf",
        ".cfg",
        ".ini",
        ".properties",
        ".preferences",
    };

    public string Name => "yaml";

    // Run before INI to avoid unix-conf/env-file stealing YAML payloads.
    public int Priority => 160;

    public string Version => "0.0.3";

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var extension = PathText.SuffixLower(path);
        var isYamlExtension = YamlExtensions.Contains(extension);
        var reasons = new List<string>();
        if (isYamlExtension)
        {
            reasons.Add($"File extension {extension} suggests YAML content");
        }

        var lines = TextLines.SplitLines(text);
        var scanText = ScanText(text, lines);
        var keys = KeyColonMatches(scanText);
        var signals = new Signals(
            isYamlExtension,
            HasKeyColon: keys.Count > 0,
            HasIndented: HasIndentedBlock(scanText),
            HasList: HasListMarker(scanText),
            HasDoc: HasMarkerLine(scanText, "---"),
            HasDocEnd: HasMarkerLine(scanText, "..."));

        var metadata = new JsonObject();
        var reviewReasons = ReviewIndentation(text, lines, metadata);

        // Guard against common false positives like inline URLs with ':' by requiring either indentation-based
        // maps or multiple key: lines.
        var keyCount = keys.Count;
        var strongStructure = signals.HasIndented || keyCount >= 3 || (signals.HasKeyColon && signals.HasList);

        // Avoid claiming common INI-like extensions unless YAML structure is strong.
        var weakYaml = !(signals.HasDoc || signals.HasIndented || (signals.HasKeyColon && signals.HasList && keyCount >= 5));
        if (IniLikeExtensions.Contains(extension) && !isYamlExtension && weakYaml)
        {
            strongStructure = false;
        }

        AddSignalReasons(reasons, signals);

        // Do not allow extension-only detection: require at least one structural YAML signal even when the
        // extension suggests YAML.
        var extensionBacked = isYamlExtension && (signals.HasKeyColon || signals.HasList || signals.HasIndented || signals.HasDoc);
        if (!(strongStructure || extensionBacked))
        {
            return CommentedReferenceMatch(text, PathText.NameLower(path), reasons);
        }

        return BuildMatch(scanText, keys, signals, reasons, metadata, reviewReasons);
    }

    // Skip leading blank/comment lines within the sampled text; the join normalises line breaks to \n only when
    // something was skipped.
    private static string ScanText(string text, IReadOnlyList<string> lines)
    {
        var start = 0;
        while (start < lines.Count)
        {
            var stripped = EngineText.StripStart(lines[start]);
            if (stripped.Length == 0 || stripped[0] == '#')
            {
                start++;
                continue;
            }

            break;
        }

        return start > 0 ? string.Join("\n", lines.Skip(start)) : text;
    }

    // The tab oddity is judged on the full text; the indentation profile is stored before gating.
    private static List<string> ReviewIndentation(string text, IReadOnlyList<string> lines, JsonObject metadata)
    {
        var reviewReasons = new List<string>();
        if (text.Contains('\t', StringComparison.Ordinal))
        {
            reviewReasons.Add(TabReviewReason);
        }

        var indentProfile = AnalyseIndentation(lines);
        if (indentProfile is null)
        {
            return reviewReasons;
        }

        metadata["indentation"] = indentProfile;
        if (indentProfile.ContainsKey("outlier_lines"))
        {
            reviewReasons.Add("Indentation widths outside tolerated range detected");
        }

        var style = (string)indentProfile["style"]!;
        if (style is "tabs" or "mixed" && !reviewReasons.Contains(TabReviewReason, StringComparer.Ordinal))
        {
            reviewReasons.Add("Tab or mixed indentation detected in YAML sample");
        }

        return reviewReasons;
    }

    private readonly record struct Signals(bool IsYamlExtension, bool HasKeyColon, bool HasIndented, bool HasList, bool HasDoc, bool HasDocEnd);

    private static void AddSignalReasons(List<string> reasons, Signals signals)
    {
        if (signals.HasDoc)
        {
            reasons.Add("Detected YAML document start marker '---'");
        }

        if (signals.HasDocEnd)
        {
            reasons.Add("Detected YAML document end marker '...'");
        }

        if (signals.HasList)
        {
            reasons.Add("Detected YAML list marker '- '");
        }

        if (signals.HasKeyColon)
        {
            reasons.Add("Found key: value pairs indicative of YAML");
        }

        if (signals.HasIndented)
        {
            reasons.Add("Found indented nested key: value blocks");
        }
    }

    // Heuristic for heavily-commented reference files (e.g., Salt 'minion').
    private DetectionMatch? CommentedReferenceMatch(string text, string lowerName, List<string> reasons)
    {
        if (!string.Equals(lowerName, "minion", StringComparison.Ordinal) || CountCommentedKeys(text) < CommentedKeyThreshold)
        {
            return null;
        }

        reasons.Add("Found numerous commented YAML key: value examples");
        return new DetectionMatch(Name, "yaml", "generic", 0.56, reasons, null);
    }

    private DetectionMatch BuildMatch(
        string scanText,
        List<string> keys,
        Signals signals,
        List<string> reasons,
        JsonObject metadata,
        List<string> reviewReasons)
    {
        string variant;
        if (HasKeyWithValue(scanText, "apiVersion") && HasKeyWithValue(scanText, "kind"))
        {
            variant = "kubernetes-manifest";
            reasons.Add("Detected apiVersion and kind keys typical of Kubernetes");
        }
        else
        {
            variant = "generic";
        }

        var confidence = Confidence(signals, variant);

        var tops = TopLevelKeyPreview(keys);
        if (tops.Count > 0)
        {
            metadata["top_level_keys_preview"] = JsonNodes.Strings(tops);
        }

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = JsonNodes.Strings(reviewReasons);
        }

        return new DetectionMatch(
            Name,
            "yaml",
            variant,
            confidence,
            reasons.Count > 0 ? reasons : ["YAML structure detected"],
            metadata);
    }

    private static double Confidence(Signals signals, string variant)
    {
        var confidence = 0.5;
        if (signals.IsYamlExtension)
        {
            confidence += 0.15;
        }

        if (signals.HasKeyColon)
        {
            confidence += 0.1;
        }

        if (signals.HasIndented)
        {
            confidence += 0.1;
        }

        if (signals.HasList)
        {
            confidence += 0.05;
        }

        if (signals.HasDoc)
        {
            confidence += 0.05;
        }

        if (signals.HasDocEnd)
        {
            confidence += 0.02;
        }

        if (string.Equals(variant, "kubernetes-manifest", StringComparison.Ordinal))
        {
            confidence += 0.05;
        }

        return Math.Min(0.95, confidence);
    }

    // Distinct keys in match order; the cap is checked after every match, including repeats.
    private static List<string> TopLevelKeyPreview(List<string> keys)
    {
        var tops = new List<string>();
        foreach (var key in keys)
        {
            if (key.Length > 0 && !tops.Contains(key, StringComparer.Ordinal))
            {
                tops.Add(key);
            }

            if (tops.Count >= TopLevelKeyPreviewLimit)
            {
                break;
            }
        }

        return tops;
    }
}
