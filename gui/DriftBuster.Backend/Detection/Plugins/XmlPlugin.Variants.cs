using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>The variant ladder for the general branch: namespaces, MSBuild, extension hints, vendor roots, fallbacks.</summary>
public sealed partial class XmlPlugin
{
    private static readonly HashSet<string> MsbuildExtensions = new(StringComparer.Ordinal)
    {
        ".targets",
        ".props",
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".vcxproj",
        ".vcproj",
        ".proj",
        ".msbuildproj",
    };

    private static readonly Dictionary<string, (string FormatName, string Variant, string Reason, double Confidence)> VendorConfigRoots =
        new(StringComparer.Ordinal)
        {
            ["nlog"] = ("structured-config-xml", "nlog-config", "Root element indicates NLog logging configuration", 0.82),
            ["log4net"] = ("structured-config-xml", "log4net-config", "Root element indicates log4net logging configuration", 0.82),
            ["serilog"] = ("structured-config-xml", "serilog-config", "Root element indicates Serilog logging configuration", 0.82),
        };

    private static (string FormatName, string Variant, double Confidence) GuessVariant(
        string extension,
        string text,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue("namespaces", out var namespacesObject) && namespacesObject is OrderedDictionary<string, object?> namespaces)
        {
            if (metadata.TryGetValue("root_namespace", out var rootNamespace) && rootNamespace is string rootNs && rootNs.Length > 0)
            {
                var byRoot = NamespaceVariant(rootNs, 0.82, reasons);
                if (byRoot is not null)
                {
                    return byRoot.Value;
                }
            }

            if (namespaces.TryGetValue("default", out var defaultNamespace) && defaultNamespace is string defaultNs && defaultNs.Length > 0)
            {
                var byDefault = NamespaceVariant(defaultNs, 0.8, reasons);
                if (byDefault is not null)
                {
                    return byDefault.Value;
                }
            }
        }

        if (LooksLikeMsbuild(extension, metadata))
        {
            return MsbuildVariant(extension, reasons, metadata);
        }

        return ExtensionVariant(extension, text, reasons, metadata);
    }

    private static (string FormatName, string Variant, double Confidence)? NamespaceVariant(string ns, double confidence, List<string> reasons)
    {
        if (HasManifestNamespace(ns))
        {
            reasons.Add("Matched assembly manifest namespace");
            return ("xml", "app-manifest-xml", confidence);
        }

        if (HasResxSchema(ns))
        {
            reasons.Add("Detected .resx schema reference");
            return ("xml", "resource-xml", confidence);
        }

        if (HasXamlNamespace(ns))
        {
            reasons.Add("Found XAML namespace declaration");
            return ("xml", "interface-xml", confidence);
        }

        return null;
    }

    private static (string FormatName, string Variant, double Confidence) MsbuildVariant(
        string extension,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata)
    {
        var kind = metadata.TryGetValue("msbuild_kind", out var existing) && existing is string existingKind && existingKind.Length > 0
            ? existingKind
            : ClassifyMsbuildKind(extension);
        metadata.TryAdd("msbuild_detected", true);
        metadata.TryAdd("msbuild_kind", kind);
        var (reason, confidence, variant) = kind switch
        {
            "targets" => ("Root element <Project> indicates an MSBuild targets layout", 0.83, "msbuild-targets"),
            "props" => ("Root element <Project> indicates an MSBuild props layout", 0.82, "msbuild-props"),
            "project" => ("Root element <Project> indicates an MSBuild project definition", 0.83, "msbuild-project"),
            _ => ("Root element <Project> indicates an MSBuild project definition", 0.82, "msbuild-project"),
        };
        AddReason(reasons, reason);
        return ("xml", variant, confidence);
    }

    private static (string FormatName, string Variant, double Confidence) ExtensionVariant(
        string extension,
        string text,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata)
    {
        var manifestMatch = HasManifestNamespace(text);
        if (string.Equals(extension, ".manifest", StringComparison.Ordinal) || manifestMatch)
        {
            if (manifestMatch)
            {
                reasons.Add("Matched assembly manifest namespace");
            }

            return ("xml", "app-manifest-xml", 0.8);
        }

        var resxMatch = HasResxSchema(text);
        if (string.Equals(extension, ".resx", StringComparison.Ordinal) || resxMatch)
        {
            if (resxMatch)
            {
                reasons.Add("Detected .resx schema reference");
            }

            return ("xml", "resource-xml", 0.8);
        }

        var xamlMatch = HasXamlNamespace(text);
        if (string.Equals(extension, ".xaml", StringComparison.Ordinal) || xamlMatch)
        {
            if (xamlMatch)
            {
                reasons.Add("Found XAML namespace declaration");
            }

            return ("xml", "interface-xml", 0.8);
        }

        var xsltMatch = HasXsltNamespace(text);
        var xsltExtension = extension is ".xsl" or ".xslt";
        if (xsltExtension || xsltMatch)
        {
            return XsltVariant(extension, xsltExtension, xsltMatch, reasons, metadata);
        }

        return RootVariant(extension, text, reasons, metadata);
    }

    private static (string FormatName, string Variant, double Confidence) XsltVariant(
        string extension,
        bool xsltExtension,
        bool xsltMatch,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata)
    {
        if (xsltExtension)
        {
            AddReason(reasons, $"File extension {extension} is commonly used for XSLT stylesheets");
        }

        if (xsltMatch)
        {
            AddReason(reasons, "Detected XSLT namespace declaration");
        }

        if (metadata.TryGetValue("root_local_name", out var rootLocal)
            && rootLocal is string local
            && EngineText.Lower(local) is "stylesheet" or "transform")
        {
            AddReason(reasons, $"Root element <{local}> indicates an XSLT stylesheet");
        }

        metadata.TryAdd("xslt_stylesheet", true);
        return ("xml", "xslt-xml", 0.82);
    }

    private static (string FormatName, string Variant, double Confidence) RootVariant(
        string extension,
        string text,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue("root_local_name", out var rootLocalObject) && rootLocalObject is string rootLocal)
        {
            var lowered = EngineText.Lower(rootLocal);
            if (string.Equals(lowered, "configuration", StringComparison.Ordinal))
            {
                AddReason(reasons, "Root element indicates framework configuration layout");
                var (variant, baseConfidence) = ClassifyConfigVariant(null, text, reasons, metadata);
                return ("structured-config-xml", variant, baseConfidence);
            }

            if (VendorConfigRoots.TryGetValue(lowered, out var vendor))
            {
                AddReason(reasons, vendor.Reason);
                return (vendor.FormatName, vendor.Variant, vendor.Confidence);
            }
        }

        if (string.Equals(extension, ".config", StringComparison.Ordinal))
        {
            if (metadata.TryGetValue("root_tag", out var root) && root is string rootTag && rootTag.Length > 0)
            {
                reasons.Add($"Root element <{rootTag}> is not the standard <configuration>");
            }

            reasons.Add("Treating as vendor-specific .config XML");
            return ("structured-config-xml", "custom-config-xml", 0.7);
        }

        return ("xml", "generic", 0.65);
    }

    private static bool LooksLikeMsbuild(string extension, OrderedDictionary<string, object?> metadata)
    {
        var loweredExtension = EngineText.Lower(extension);
        if (MsbuildExtensions.Contains(loweredExtension))
        {
            return true;
        }

        if (!metadata.TryGetValue("root_local_name", out var rootLocal)
            || rootLocal is not string local
            || !string.Equals(EngineText.Lower(local), "project", StringComparison.Ordinal))
        {
            return false;
        }

        if (metadata.TryGetValue("root_namespace", out var ns) && ns is string nsText && HasMsbuildNamespace(nsText))
        {
            return true;
        }

        if (metadata.TryGetValue("namespaces", out var namespaces)
            && namespaces is OrderedDictionary<string, object?> namespaceMap
            && namespaceMap.TryGetValue("default", out var defaultNs)
            && defaultNs is string defaultText
            && HasMsbuildNamespace(defaultText))
        {
            return true;
        }

        if (metadata.TryGetValue("root_attributes", out var attributes) && attributes is OrderedDictionary<string, object?> attributeMap)
        {
            foreach (var name in attributeMap.Keys)
            {
                if (EngineText.Lower(name) is "defaulttargets" or "toolsversion" or "sdk")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ClassifyMsbuildKind(string extension)
    {
        var loweredExtension = EngineText.Lower(extension);
        return loweredExtension switch
        {
            ".targets" => "targets",
            ".props" => "props",
            _ => "project",
        };
    }
}
