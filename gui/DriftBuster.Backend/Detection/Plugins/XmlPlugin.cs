using System.Text;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// XML heuristics in a fixed order: framework configs by filename and section hints (transforms before vendor fallbacks),
/// then namespace-driven manifest, resx and XAML variants, then extension and structure fallbacks; root and namespace
/// metadata are captured throughout.
/// </summary>
/// <remarks>
/// The 4096-character metadata snippet and the 512 KiB parse cap are counted in code points. Patterns are hand-matched in
/// <c>XmlPlugin.Patterns.cs</c>. A tree-parse failure only removes the tree; the pattern-based paths continue.
/// </remarks>
public sealed partial class XmlPlugin : IFormatPlugin
{
    private const int SnippetChars = 4096;

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.Ordinal)
    {
        ".xml",
        ".manifest",
        ".resx",
        ".xaml",
        ".config",
        ".xsl",
        ".xslt",
        ".targets",
    };

    public string Name => "xml";

    public int Priority => 100;

    public string Version => "0.0.6";

    /// <summary>Code-point cap for the tree parse and well-formedness probe (test seam).</summary>
    internal int MaxSafeParseChars { get; set; } = 512 * 1024;

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var extension = PathText.SuffixLower(path);
        var reasons = new List<string>();

        var metadata = CollectMetadata(text, extension);

        if (string.Equals(extension, ".config", StringComparison.Ordinal))
        {
            var configMatch = DetectConfig(path, text, reasons, metadata);
            if (configMatch is not null)
            {
                return configMatch;
            }
        }

        return DetectGeneral(extension, text, reasons, metadata);
    }

    private DetectionMatch? DetectConfig(string path, string text, List<string> reasons, JsonObject metadata)
    {
        var configRoot = false;
        if (HasConfigurationElement(text))
        {
            reasons.Add("Found <configuration> root element");
            configRoot = true;
        }
        else if (metadata.Text("root_local_name") is { } local
            && string.Equals(local.ToLowerInvariant(), "configuration", StringComparison.Ordinal))
        {
            reasons.Add("Root element indicates framework configuration layout");
            configRoot = true;
        }

        if (!configRoot)
        {
            return null;
        }

        if (HasElementNamed(text, ConfigSectionKeywords))
        {
            reasons.Add("Matched known configuration section tags used by web frameworks");
        }

        if (metadata.Text("root_tag") is { } rootTag && rootTag.Length > 0)
        {
            AddReason(reasons, $"Detected root element <{rootTag}>");
        }

        AppendDeclarationReasons(metadata, reasons);
        AppendNamespaceReason(metadata, reasons);
        AppendSchemaReason(metadata, reasons);
        AppendResxReason(metadata, reasons);
        AppendMsbuildReasons(metadata, reasons);
        AppendAttributeHintReasons(metadata, reasons);
        AppendDoctypeReason(metadata, reasons);
        var (variant, baseConfidence) = ClassifyConfigVariant(path, text, reasons, metadata);
        var confidence = Math.Min(0.95, baseConfidence + ConfidenceBonus(metadata, foundElements: true));
        return new DetectionMatch(Name, "structured-config-xml", variant, confidence, reasons, metadata.Count == 0 ? null : metadata);
    }

    private DetectionMatch? DetectGeneral(string extension, string text, List<string> reasons, JsonObject metadata)
    {
        var elementMatch = HasGenericElement(text);
        var hasXmlDeclaration = TryXmlDeclaration(text, out _);
        var extensionHint = XmlExtensions.Contains(extension);
        if (!extensionHint && !hasXmlDeclaration && !elementMatch)
        {
            return null;
        }

        if (extensionHint)
        {
            reasons.Add($"File extension {extension} suggests XML content");
        }

        AppendDeclarationReasons(metadata, reasons);
        if (elementMatch)
        {
            reasons.Add("Found XML element structure");
        }

        var (formatName, variant, baseConfidence) = GuessVariant(extension, text, reasons, metadata);
        ProbeWellFormed(text, reasons, metadata);
        if (metadata.Text("root_tag") is { } rootTag && rootTag.Length > 0)
        {
            reasons.Add($"Detected root element <{rootTag}>");
        }

        AppendNamespaceReason(metadata, reasons);
        AppendSchemaReason(metadata, reasons);
        AppendResxReason(metadata, reasons);
        AppendMsbuildReasons(metadata, reasons);
        AppendAttributeHintReasons(metadata, reasons);
        AppendDoctypeReason(metadata, reasons);
        var bonus = ConfidenceBonus(metadata, foundElements: elementMatch || metadata.HasContent("root_tag"));
        return new DetectionMatch(
            Name,
            formatName,
            variant,
            Math.Min(0.95, baseConfidence + bonus),
            reasons.Count == 0 ? ["File extension indicates XML"] : reasons,
            metadata.Count == 0 ? null : metadata);
    }

    private void ProbeWellFormed(string text, List<string> reasons, JsonObject metadata)
    {
        var sampleText = text[..PrefixLength(text, MaxSafeParseChars)];
        if (IsWellFormed(sampleText))
        {
            metadata["xml_well_formed"] = true;
            return;
        }

        metadata["xml_well_formed"] = false;
        reasons.Add("XML appears not well-formed within sampled content");
        metadata.TryAdd("needs_review", true);
        if (metadata.Array("review_reasons") is not { } reviewReasons)
        {
            reviewReasons = [];
            metadata["review_reasons"] = reviewReasons;
        }

        reviewReasons.Add("XML not well-formed");
    }

    private static void AddReason(List<string> reasons, string message)
    {
        if (!reasons.Contains(message, StringComparer.Ordinal))
        {
            reasons.Add(message);
        }
    }

    private static double ConfidenceBonus(JsonObject metadata, bool foundElements)
    {
        var bonus = 0.0;
        if (metadata.ContainsKey("xml_declaration"))
        {
            bonus += 0.05;
        }

        if (foundElements)
        {
            bonus += 0.05;
        }

        if (metadata.HasContent("root_tag"))
        {
            bonus += 0.03;
        }

        if (metadata.HasContent("namespaces"))
        {
            bonus += 0.02;
        }

        if (metadata.HasContent("doctype"))
        {
            bonus += 0.02;
        }

        if (metadata.HasContent("root_attributes"))
        {
            bonus += 0.01;
        }

        if (metadata.HasContent("config_transform"))
        {
            bonus += 0.01;
        }

        if (metadata.HasContent("schema_locations"))
        {
            bonus += 0.02;
        }

        if (metadata.HasContent("resource_keys"))
        {
            bonus += 0.01;
        }

        if (metadata.Object("attribute_hints") is { } hintMap
            && hintMap.Any(hint => JsonObjectReading.HasContent(hint.Value)))
        {
            bonus += 0.01;
        }

        return MsbuildBonus(metadata, bonus);
    }

    /// <summary>Adds each MSBuild increment to the running <paramref name="bonus"/> in a fixed order; doubles are not associative.</summary>
    private static double MsbuildBonus(JsonObject metadata, double bonus)
    {
        if (!metadata.HasContent("msbuild_detected"))
        {
            return bonus;
        }

        if (metadata.HasContent("msbuild_default_targets"))
        {
            bonus += 0.01;
        }

        if (metadata.HasContent("msbuild_sdk"))
        {
            bonus += 0.005;
        }

        if (metadata.HasContent("msbuild_import_hints"))
        {
            bonus += 0.01;
        }

        if (metadata.HasContent("msbuild_targets"))
        {
            bonus += 0.01;
        }

        return bonus;
    }

    /// <summary>Number of code points in <paramref name="text"/>.</summary>
    private static int CodePointCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>UTF-16 length of the first <paramref name="codePoints"/> code points.</summary>
    private static int PrefixLength(string text, int codePoints)
    {
        var offset = 0;
        var seen = 0;
        while (offset < text.Length && seen < codePoints)
        {
            Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out var consumed);
            offset += consumed;
            seen++;
        }

        return offset;
    }
}
