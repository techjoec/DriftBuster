using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// XML detection heuristics for configuration-style and generic documents. Detection follows a strict order:
/// framework configuration payloads by filename and core section hints (transforms surfaced before vendor
/// fallbacks), then namespace-driven manifest, resx and XAML variants, then extension and structure fallbacks,
/// capturing the root element and namespace metadata for reporting adapters throughout.
/// </summary>
/// <remarks>
/// Windows are measured in code points like Python <c>str</c> slicing: the 4096-character metadata snippet and the
/// 512 KiB parse cap. Every module regex is hand-matched on code points in <c>XmlPlugin.Patterns.cs</c>. The tree
/// parse and the well-formedness probe are <see cref="DefusedXmlParser"/>, which accepts what defusedxml over expat
/// accepts and never reads anything external; on any parse failure the tree is absent and the regex-only paths
/// continue.
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

    /// <summary>Length cap (in code points) for the tree parse and the well-formedness probe; a test seam mirroring the Python class attribute.</summary>
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

        // Prefer .config specific detection first.
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

    private DetectionMatch? DetectConfig(string path, string text, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        var configRoot = false;
        if (HasConfigurationElement(text))
        {
            reasons.Add("Found <configuration> root element");
            configRoot = true;
        }
        else if (metadata.TryGetValue("root_local_name", out var rootLocal)
            && rootLocal is string local
            && string.Equals(PythonText.Lower(local), "configuration", StringComparison.Ordinal))
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

        if (metadata.TryGetValue("root_tag", out var root) && root is string rootTag && rootTag.Length > 0)
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

    private DetectionMatch? DetectGeneral(string extension, string text, List<string> reasons, OrderedDictionary<string, object?> metadata)
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
        if (metadata.TryGetValue("root_tag", out var root) && root is string rootTag && rootTag.Length > 0)
        {
            reasons.Add($"Detected root element <{rootTag}>");
        }

        AppendNamespaceReason(metadata, reasons);
        AppendSchemaReason(metadata, reasons);
        AppendResxReason(metadata, reasons);
        AppendMsbuildReasons(metadata, reasons);
        AppendAttributeHintReasons(metadata, reasons);
        AppendDoctypeReason(metadata, reasons);
        var bonus = ConfidenceBonus(metadata, foundElements: elementMatch || IsTruthy(metadata, "root_tag"));
        return new DetectionMatch(
            Name,
            formatName,
            variant,
            Math.Min(0.95, baseConfidence + bonus),
            reasons.Count == 0 ? ["File extension indicates XML"] : reasons,
            metadata.Count == 0 ? null : metadata);
    }

    // Optional well-formedness check within safe bounds.
    private void ProbeWellFormed(string text, List<string> reasons, OrderedDictionary<string, object?> metadata)
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
        if (!metadata.TryGetValue("review_reasons", out var existing))
        {
            existing = new List<string>();
            metadata["review_reasons"] = existing;
        }

        if (existing is List<string> reviewReasons)
        {
            reviewReasons.Add("XML not well-formed");
        }
    }

    private static void AddReason(List<string> reasons, string message)
    {
        if (!reasons.Contains(message, StringComparer.Ordinal))
        {
            reasons.Add(message);
        }
    }

    private static double ConfidenceBonus(OrderedDictionary<string, object?> metadata, bool foundElements)
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

        if (IsTruthy(metadata, "root_tag"))
        {
            bonus += 0.03;
        }

        if (IsTruthy(metadata, "namespaces"))
        {
            bonus += 0.02;
        }

        if (IsTruthy(metadata, "doctype"))
        {
            bonus += 0.02;
        }

        if (IsTruthy(metadata, "root_attributes"))
        {
            bonus += 0.01;
        }

        if (IsTruthy(metadata, "config_transform"))
        {
            bonus += 0.01;
        }

        if (IsTruthy(metadata, "schema_locations"))
        {
            bonus += 0.02;
        }

        if (IsTruthy(metadata, "resource_keys"))
        {
            bonus += 0.01;
        }

        if (metadata.TryGetValue("attribute_hints", out var hints)
            && hints is OrderedDictionary<string, object?> hintMap
            && hintMap.Values.Any(IsTruthyValue))
        {
            bonus += 0.01;
        }

        return MsbuildBonus(metadata, bonus);
    }

    /// <summary>Adds each MSBuild increment to the running <paramref name="bonus"/> in Python order; doubles are not associative.</summary>
    private static double MsbuildBonus(OrderedDictionary<string, object?> metadata, double bonus)
    {
        if (!IsTruthy(metadata, "msbuild_detected"))
        {
            return bonus;
        }

        if (IsTruthy(metadata, "msbuild_default_targets"))
        {
            bonus += 0.01;
        }

        if (IsTruthy(metadata, "msbuild_sdk"))
        {
            bonus += 0.005;
        }

        if (IsTruthy(metadata, "msbuild_import_hints"))
        {
            bonus += 0.01;
        }

        if (IsTruthy(metadata, "msbuild_targets"))
        {
            bonus += 0.01;
        }

        return bonus;
    }

    /// <summary>Python truthiness of <c>metadata.get(key)</c> for the value shapes this plugin stores.</summary>
    private static bool IsTruthy(OrderedDictionary<string, object?> metadata, string key)
        => metadata.TryGetValue(key, out var value) && IsTruthyValue(value);

    private static bool IsTruthyValue(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int number => number != 0,
        System.Collections.ICollection collection => collection.Count > 0,
        _ => true,
    };

    /// <summary>Number of code points in <paramref name="text"/>, matching Python <c>len(str)</c>.</summary>
    private static int CodePointCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>The UTF-16 length of the first <paramref name="codePoints"/> code points, matching <c>text[:n]</c>.</summary>
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
