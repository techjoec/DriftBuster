namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Reason strings derived from collected metadata, deduplicated in Python emission order.</summary>
public sealed partial class XmlPlugin
{
    private static void AppendDeclarationReasons(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!metadata.TryGetValue("xml_declaration", out var declaration))
        {
            return;
        }

        AddReason(reasons, "Detected XML declaration");
        if (declaration is not OrderedDictionary<string, object?> attributes)
        {
            return;
        }

        if (attributes.TryGetValue("version", out var version) && version is string versionText && versionText.Length > 0)
        {
            AddReason(reasons, $"XML version declared as {versionText}");
        }

        if (attributes.TryGetValue("encoding", out var encoding) && encoding is string encodingText && encodingText.Length > 0)
        {
            AddReason(reasons, $"XML declared encoding {encodingText}");
        }

        if (attributes.TryGetValue("standalone", out var standalone) && standalone is string standaloneText && standaloneText.Length > 0)
        {
            AddReason(reasons, $"XML standalone flag is {standaloneText}");
        }
    }

    private static void AppendNamespaceReason(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!metadata.TryGetValue("namespaces", out var namespacesObject)
            || namespacesObject is not OrderedDictionary<string, object?> namespaces
            || namespaces.Count == 0)
        {
            return;
        }

        var previewEntries = new List<string>();
        if (metadata.TryGetValue("namespace_provenance", out var provenance) && provenance is List<OrderedDictionary<string, object?>> entries)
        {
            foreach (var entry in entries)
            {
                if (!entry.TryGetValue("uri", out var uriObject) || uriObject is not string uri || uri.Length == 0)
                {
                    continue;
                }

                var prefixLabel = entry.TryGetValue("prefix", out var prefix) && prefix is string prefixText && prefixText.Length > 0
                    ? prefixText
                    : "default";
                previewEntries.Add(entry.TryGetValue("line", out var line) && line is int lineNumber && lineNumber > 0
                    ? $"{prefixLabel}→{uri} @L{lineNumber}"
                    : $"{prefixLabel}→{uri}");
                if (previewEntries.Count == 3)
                {
                    break;
                }
            }
        }

        if (previewEntries.Count > 0)
        {
            AddReason(reasons, $"Recorded XML namespace declarations ({string.Join("; ", previewEntries)})");
            return;
        }

        if (namespaces.TryGetValue("default", out var defaultNs) && defaultNs is string defaultText && defaultText.Length > 0)
        {
            AddReason(reasons, $"Detected XML namespace declarations (default namespace {defaultText})");
        }
        else
        {
            AddReason(reasons, "Detected XML namespace declarations");
        }
    }

    private static void AppendSchemaReason(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!metadata.TryGetValue("schema_locations", out var locations) || locations is not List<OrderedDictionary<string, object?>> entries)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var location = entry.TryGetValue("location", out var locationObject) ? locationObject as string : null;
            var ns = entry.TryGetValue("namespace", out var namespaceObject) ? namespaceObject as string : null;
            if (!string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(ns))
            {
                AddReason(reasons, $"Schema {location} declared for namespace {ns}");
            }
            else if (!string.IsNullOrEmpty(location))
            {
                AddReason(reasons, $"Schema {location} declared for default namespace");
            }
        }
    }

    private static void AppendResxReason(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!metadata.TryGetValue("resource_keys", out var keys) || keys is not List<string> resourceKeys || resourceKeys.Count == 0)
        {
            return;
        }

        if (metadata.TryGetValue("resource_keys_preview", out var preview) && preview is string previewText && previewText.Length > 0)
        {
            AddReason(reasons, $"Captured resource keys from .resx payload (e.g., {previewText})");
        }
        else
        {
            AddReason(reasons, "Captured resource keys from .resx payload");
        }
    }

    private static void AppendMsbuildReasons(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!IsTruthy(metadata, "msbuild_detected"))
        {
            return;
        }

        if (metadata.TryGetValue("msbuild_default_targets", out var defaults) && defaults is List<string> defaultTargets && defaultTargets.Count > 0)
        {
            AddReason(reasons, $"MSBuild default targets declared ({string.Join(", ", defaultTargets.Take(3))})");
        }

        if (metadata.TryGetValue("msbuild_tools_version", out var tools) && tools is string toolsVersion && toolsVersion.Length > 0)
        {
            AddReason(reasons, $"MSBuild ToolsVersion set to {toolsVersion}");
        }

        if (metadata.TryGetValue("msbuild_sdk", out var sdkObject) && sdkObject is string sdk && sdk.Length > 0)
        {
            AddReason(reasons, $"MSBuild SDK specified ({sdk})");
        }

        if (metadata.TryGetValue("msbuild_targets", out var targetsObject) && targetsObject is List<string> targets && targets.Count > 0)
        {
            AddReason(reasons, $"Captured MSBuild target declarations ({string.Join(", ", targets.Take(3))})");
        }

        if (metadata.TryGetValue("msbuild_import_hints", out var imports) && imports is List<OrderedDictionary<string, object?>> importHints && importHints.Count > 0)
        {
            AddReason(reasons, "Captured MSBuild import references");
        }
    }

    private static void AppendAttributeHintReasons(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (!metadata.TryGetValue("attribute_hints", out var hintsObject) || hintsObject is not OrderedDictionary<string, object?> hints || hints.Count == 0)
        {
            return;
        }

        foreach (var (key, message) in new[]
        {
            ("connection_strings", "Captured connection string attribute hints"),
            ("service_endpoints", "Captured service endpoint attribute hints"),
            ("feature_flags", "Captured feature flag attribute hints"),
        })
        {
            if (hints.TryGetValue(key, out var entries) && entries is List<OrderedDictionary<string, object?>> list && list.Count > 0)
            {
                AddReason(reasons, message);
            }
        }
    }

    private static void AppendDoctypeReason(OrderedDictionary<string, object?> metadata, List<string> reasons)
    {
        if (metadata.TryGetValue("doctype", out var doctype) && doctype is string doctypeText && doctypeText.Length > 0)
        {
            AddReason(reasons, $"Document declares DOCTYPE {doctypeText}");
        }
    }
}
