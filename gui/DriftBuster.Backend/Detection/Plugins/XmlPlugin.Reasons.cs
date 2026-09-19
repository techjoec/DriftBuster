using System.Text.Json.Nodes;

using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Reason strings derived from collected metadata, deduplicated in emission order.</summary>
public sealed partial class XmlPlugin
{
    private static void AppendDeclarationReasons(JsonObject metadata, List<string> reasons)
    {
        if (!metadata.ContainsKey("xml_declaration"))
        {
            return;
        }

        AddReason(reasons, "Detected XML declaration");
        if (metadata.Object("xml_declaration") is not { } attributes)
        {
            return;
        }

        if (attributes.Text("version") is { } versionText && versionText.Length > 0)
        {
            AddReason(reasons, $"XML version declared as {versionText}");
        }

        if (attributes.Text("encoding") is { } encodingText && encodingText.Length > 0)
        {
            AddReason(reasons, $"XML declared encoding {encodingText}");
        }

        if (attributes.Text("standalone") is { } standaloneText && standaloneText.Length > 0)
        {
            AddReason(reasons, $"XML standalone flag is {standaloneText}");
        }
    }

    private static void AppendNamespaceReason(JsonObject metadata, List<string> reasons)
    {
        if (metadata.Object("namespaces") is not { } namespaces
            || namespaces.Count == 0)
        {
            return;
        }

        var previewEntries = new List<string>();
        if (metadata.Array("namespace_provenance") is { } entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry.Text("uri") is not { } uri || uri.Length == 0)
                {
                    continue;
                }

                var prefixLabel = entry.Text("prefix") is { } prefixText && prefixText.Length > 0
                    ? prefixText
                    : "default";
                previewEntries.Add(entry.Int("line") is { } lineNumber && lineNumber > 0
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

        if (namespaces.Text("default") is { } defaultText && defaultText.Length > 0)
        {
            AddReason(reasons, $"Detected XML namespace declarations (default namespace {defaultText})");
        }
        else
        {
            AddReason(reasons, "Detected XML namespace declarations");
        }
    }

    private static void AppendSchemaReason(JsonObject metadata, List<string> reasons)
    {
        if (metadata.Array("schema_locations") is not { } entries)
        {
            return;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            var location = entry.Text("location");
            var ns = entry.Text("namespace");
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

    private static void AppendResxReason(JsonObject metadata, List<string> reasons)
    {
        if (metadata.Strings("resource_keys").Count == 0)
        {
            return;
        }

        if (metadata.Text("resource_keys_preview") is { } previewText && previewText.Length > 0)
        {
            AddReason(reasons, $"Captured resource keys from .resx payload (e.g., {previewText})");
        }
        else
        {
            AddReason(reasons, "Captured resource keys from .resx payload");
        }
    }

    private static void AppendMsbuildReasons(JsonObject metadata, List<string> reasons)
    {
        if (!metadata.HasContent("msbuild_detected"))
        {
            return;
        }

        if (metadata.Strings("msbuild_default_targets") is { Count: > 0 } defaultTargets)
        {
            AddReason(reasons, $"MSBuild default targets declared ({string.Join(", ", defaultTargets.Take(3))})");
        }

        if (metadata.Text("msbuild_tools_version") is { } toolsVersion && toolsVersion.Length > 0)
        {
            AddReason(reasons, $"MSBuild ToolsVersion set to {toolsVersion}");
        }

        if (metadata.Text("msbuild_sdk") is { } sdk && sdk.Length > 0)
        {
            AddReason(reasons, $"MSBuild SDK specified ({sdk})");
        }

        if (metadata.Strings("msbuild_targets") is { Count: > 0 } targets)
        {
            AddReason(reasons, $"Captured MSBuild target declarations ({string.Join(", ", targets.Take(3))})");
        }

        if (metadata.Array("msbuild_import_hints") is { Count: > 0 })
        {
            AddReason(reasons, "Captured MSBuild import references");
        }
    }

    private static void AppendAttributeHintReasons(JsonObject metadata, List<string> reasons)
    {
        if (metadata.Object("attribute_hints") is not { } hints || hints.Count == 0)
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
            if (hints.Array(key) is { Count: > 0 })
            {
                AddReason(reasons, message);
            }
        }
    }

    private static void AppendDoctypeReason(JsonObject metadata, List<string> reasons)
    {
        if (metadata.Text("doctype") is { } doctypeText && doctypeText.Length > 0)
        {
            AddReason(reasons, $"Document declares DOCTYPE {doctypeText}");
        }
    }
}
