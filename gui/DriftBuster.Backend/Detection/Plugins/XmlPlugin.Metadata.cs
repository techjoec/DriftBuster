using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Metadata collection over the first 4096 code points: declaration, doctype, root tag and attributes, namespace
/// declarations with provenance, schema locations, then the tree-derived resx, attribute-hint and MSBuild facts.
/// </summary>
public sealed partial class XmlPlugin
{
    private static readonly string[] ConfigSectionKeywords = ["appsettings", "runtime", "system.web"];

    /// <summary>Orders (key, value) pairs by lowercased key, then key, by code point; used with a stable sort.</summary>
    private sealed class EngineKeyOrder : IComparer<(string Key, string Value)>
    {
        public static EngineKeyOrder Instance { get; } = new();

        public int Compare((string Key, string Value) x, (string Key, string Value) y)
        {
            var result = PathText.CompareCodePoints(EngineText.Lower(x.Key), EngineText.Lower(y.Key));
            return result != 0 ? result : PathText.CompareCodePoints(x.Key, y.Key);
        }
    }

    /// <summary>Collects the metadata; internal so the parse-cap test can call it directly.</summary>
    internal JsonObject CollectMetadata(string text, string extension)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(extension);
        var snippet = text[..PrefixLength(text, SnippetChars)];
        var metadata = new JsonObject();

        var rootElement = ParseTree(text);

        CollectDeclaration(snippet, metadata);

        var doctype = FindDoctypeName(snippet);
        if (doctype is not null)
        {
            metadata["doctype"] = doctype;
        }

        CollectRootTag(snippet, metadata);
        CollectNamespaces(snippet, metadata);
        ExtractSchemaLocations(metadata);

        if (rootElement is not null)
        {
            ExtractResxKeys(rootElement, metadata);
            ExtractAttributeHints(rootElement, metadata);
            ExtractMsbuildMetadata(rootElement, metadata, extension);
        }
        else if (LooksLikeMsbuild(extension, metadata))
        {
            metadata["msbuild_detected"] = true;
            metadata["msbuild_kind"] = ClassifyMsbuildKind(extension);
        }

        return metadata;
    }

    private static void CollectDeclaration(string snippet, JsonObject metadata)
    {
        if (!TryXmlDeclaration(snippet, out var attrsSegment))
        {
            return;
        }

        var declAttrs = new JsonObject();
        foreach (var (name, value) in AttributeMatches(attrsSegment))
        {
            declAttrs[EngineText.Lower(name)] = value;
        }

        metadata["xml_declaration"] = declAttrs;
        if (declAttrs.Text("encoding") is { } encodingText && encodingText.Length > 0)
        {
            metadata.TryAdd("encoding", encodingText);
        }
    }

    private static void CollectRootTag(string snippet, JsonObject metadata)
    {
        var startTag = FindStartTag(snippet);
        if (startTag is null)
        {
            return;
        }

        var (name, end) = startTag.Value;
        metadata.TryAdd("root_tag", name);
        var (prefix, local) = SplitQualifiedName(name);
        if (prefix is not null)
        {
            metadata.TryAdd("root_prefix", prefix);
        }

        metadata.TryAdd("root_local_name", local);
        var attributes = ExtractRootAttributes(snippet, end);
        if (attributes.Count > 0)
        {
            metadata.TryAdd("root_attributes", attributes);
        }
    }

    private static (string? Prefix, string Local) SplitQualifiedName(string name)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var prefix = name[..colon];
            var local = name[(colon + 1)..];
            if (prefix.Length > 0 && local.Length > 0)
            {
                return (prefix, local);
            }
        }

        return (null, name);
    }

    private static void CollectNamespaces(string snippet, JsonObject metadata)
    {
        var pairs = new List<(string Key, string Value)>();
        var provenance = new List<JsonObject>();
        foreach (var match in XmlnsMatches(snippet))
        {
            var prefix = match.Prefix ?? "default";
            var uri = EngineText.Strip(match.Uri);
            pairs.Add((prefix, uri));
            provenance.Add(ProvenanceEntry(snippet, match, uri));
        }

        if (pairs.Count == 0)
        {
            return;
        }

        var namespaces = new JsonObject();
        foreach (var (prefix, uri) in pairs.OrderBy(pair => pair, EngineKeyOrder.Instance))
        {
            namespaces[prefix] = uri;
        }

        metadata["namespaces"] = namespaces;
        if (provenance.Count > 0)
        {
            metadata["namespace_provenance"] = JsonNodes.Array(provenance);
        }

        if (metadata.Text("root_prefix") is { } prefixText)
        {
            if (namespaces.Text(prefixText) is { } nsText && nsText.Length > 0)
            {
                metadata["root_namespace"] = nsText;
            }
        }
        else if (namespaces.Text("default") is { } defaultText && defaultText.Length > 0)
        {
            metadata["root_namespace"] = defaultText;
        }
    }

    private static JsonObject ProvenanceEntry(string snippet, XmlnsMatch match, string uri)
    {
        var attrStart = match.Start;
        var lineNumber = snippet.AsSpan(0, attrStart).Count('\n') + 1;
        var lastNewline = attrStart == 0 ? -1 : snippet.LastIndexOf('\n', attrStart - 1);

        // Columns are counted in code points from the line start.
        var columnNumber = CodePointCount(snippet[(lastNewline + 1)..attrStart]) + 1;
        var attributeName = match.Prefix is null ? "xmlns" : $"xmlns:{match.Prefix}";
        var digest = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{attributeName}|{uri}")))[..12];
        return new JsonObject()
        {
            ["attribute"] = attributeName,
            ["prefix"] = match.Prefix,
            ["uri"] = uri,
            ["line"] = lineNumber,
            ["column"] = columnNumber,
            ["source"] = "root-attribute",
            ["hash"] = digest,
        };
    }

    private static JsonObject ExtractRootAttributes(string snippet, int startIndex)
    {
        var result = new JsonObject();
        if (snippet.IndexOf('>', startIndex) < 0)
        {
            return result;
        }

        var segment = new StringBuilder();
        char? inQuote = null;
        for (var index = startIndex; index < snippet.Length; index++)
        {
            var ch = snippet[index];
            if (inQuote is not null)
            {
                segment.Append(ch);
                if (ch == inQuote)
                {
                    inQuote = null;
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                segment.Append(ch);
                inQuote = ch;
                continue;
            }

            if (ch == '>')
            {
                break;
            }

            segment.Append(ch);
        }

        var rawSegment = EngineText.Strip(segment.ToString());
        if (rawSegment.Length == 0)
        {
            return result;
        }

        if (rawSegment.EndsWith('/'))
        {
            rawSegment = EngineText.Strip(rawSegment[..^1]);
        }

        var items = AttributeMatches(rawSegment).Select(pair => (pair.Name, EngineText.Strip(pair.Value))).ToList();
        foreach (var (name, value) in items.OrderBy(pair => pair, EngineKeyOrder.Instance))
        {
            result[name] = value;
        }

        return result;
    }

    private static void ExtractSchemaLocations(JsonObject metadata)
    {
        if (metadata.Object("root_attributes") is not { } attributes
            || attributes.Count == 0)
        {
            return;
        }

        var entries = new List<JsonObject>();
        foreach (var (attrName, rawValue) in attributes)
        {
            if (rawValue is not JsonValue value || !value.TryGetValue<string>(out var rawText))
            {
                continue;
            }

            var colon = attrName.IndexOf(':', StringComparison.Ordinal);
            var localName = colon < 0 ? attrName : attrName[(colon + 1)..];
            var tokens = EngineText.Split(rawText);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (string.Equals(localName, "schemaLocation", StringComparison.Ordinal))
            {
                if (tokens.Count < 2)
                {
                    continue;
                }

                for (var index = 0; index < tokens.Count - 1; index += 2)
                {
                    entries.Add(SchemaEntry(tokens[index], tokens[index + 1]));
                }
            }
            else if (string.Equals(localName, "noNamespaceSchemaLocation", StringComparison.Ordinal))
            {
                entries.Add(SchemaEntry(null, string.Join(' ', tokens)));
            }
        }

        if (entries.Count > 0)
        {
            metadata["schema_locations"] = JsonNodes.Array(entries);
        }
    }

    private static JsonObject SchemaEntry(string? ns, string location)
        => new()
        {
            ["namespace"] = ns,
            ["location"] = location,
        };

    /// <summary>An attribute name as the metadata spells it: the local name, or <c>{uri}local</c> when namespaced.</summary>
    private static string ElementTreeName(XName name)
        => name.NamespaceName.Length == 0 ? name.LocalName : $"{{{name.NamespaceName}}}{name.LocalName}";

    /// <summary>An element's attributes in document order, namespace declarations excluded.</summary>
    private static OrderedDictionary<string, string> ElementAttributes(XElement element)
    {
        var attributes = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                attributes[ElementTreeName(attribute.Name)] = attribute.Value;
            }
        }

        return attributes;
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
