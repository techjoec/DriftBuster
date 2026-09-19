using System.Text.Json.Nodes;
using System.Xml.Linq;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Tree walks over <see cref="SafeXml.TryLoadRoot"/>. Payloads over the size cap are not parsed; a DOCTYPE is skipped by the
/// reader, and a reference to an entity it declares leaves the document unparsed.
/// </summary>
public sealed partial class XmlPlugin
{
    private XElement? ParseTree(string text)
    {
        var stripped = text.TrimStart();
        return stripped.Length == 0 || CodePointCount(stripped) > MaxSafeParseChars ? null : SafeXml.TryLoadRoot(stripped);
    }

    private static bool IsWellFormed(string sampleText) => SafeXml.IsWellFormed(sampleText);

    private static void ExtractResxKeys(XElement root, JsonObject metadata)
    {
        if (!string.Equals(EngineText.Lower(root.Name.LocalName), "root", StringComparison.Ordinal))
        {
            return;
        }

        string? namespaceHint = null;
        if (metadata.Text("root_namespace") is { } rootNamespaceText)
        {
            namespaceHint = rootNamespaceText;
        }
        else if (metadata.Object("namespaces") is { } namespaceMap)
        {
            namespaceHint = namespaceMap.Text("default");
        }

        if (string.IsNullOrEmpty(namespaceHint) || !HasResxSchema(namespaceHint))
        {
            return;
        }

        var resourceKeys = new List<string>();
        foreach (var element in root.DescendantsAndSelf())
        {
            if (!string.Equals(EngineText.Lower(element.Name.LocalName), "data", StringComparison.Ordinal))
            {
                continue;
            }

            var name = element.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            resourceKeys.Add(name);
            if (resourceKeys.Count >= 10)
            {
                break;
            }
        }

        if (resourceKeys.Count > 0)
        {
            metadata["resource_keys"] = JsonNodes.Strings(resourceKeys);
            metadata.TryAdd("resource_keys_preview", string.Join(", ", resourceKeys.Take(3)));
        }
    }

    /// <summary>One element's attributes plus a lowercase-to-actual name lookup (last duplicate wins).</summary>
    private sealed class ElementView
    {
        public ElementView(XElement element)
        {
            ElementName = element.Name.LocalName;
            Attributes = ElementAttributes(element);
            LowerToActual = LowerLookup(Attributes);
        }

        public string ElementName { get; }

        public OrderedDictionary<string, string> Attributes { get; }

        public Dictionary<string, string> LowerToActual { get; }

        public string? KeyAttributeName { get; set; }

        public string? KeyValue { get; set; }

        private static Dictionary<string, string> LowerLookup(OrderedDictionary<string, string> attributes)
        {
            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in attributes.Keys)
            {
                lookup[EngineText.Lower(name)] = name;
            }

            return lookup;
        }

        public string? Actual(string candidate) => LowerToActual.TryGetValue(candidate, out var actual) ? actual : null;

        public string Value(string name) => Attributes.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private sealed class HintBuckets
    {
        private static readonly string[] Categories = ["connection_strings", "service_endpoints", "feature_flags"];

        public OrderedDictionary<string, List<JsonObject>> Hints { get; } = Create();

        public Dictionary<string, HashSet<(string, string, string, string)>> Seen { get; } =
            Categories.ToDictionary(category => category, _ => new HashSet<(string, string, string, string)>(), StringComparer.Ordinal);

        private static OrderedDictionary<string, List<JsonObject>> Create()
        {
            var hints = new OrderedDictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            foreach (var category in Categories)
            {
                hints[category] = [];
            }

            return hints;
        }
    }

    private static void ExtractAttributeHints(XElement root, JsonObject metadata)
    {
        var buckets = new HintBuckets();
        foreach (var element in root.DescendantsAndSelf())
        {
            var view = new ElementView(element);
            if (view.Attributes.Count == 0)
            {
                continue;
            }

            CollectKeyAttribute(view);
            CollectConnectionHint(buckets, view);
            CollectEndpointHints(buckets, view);
            CollectFeatureHint(buckets, view);
        }

        var filtered = new JsonObject();
        foreach (var (category, entries) in buckets.Hints)
        {
            if (entries.Count > 0)
            {
                filtered[category] = JsonNodes.Array(entries);
            }
        }

        if (filtered.Count > 0)
        {
            metadata["attribute_hints"] = filtered;
        }
    }

    private static void CollectKeyAttribute(ElementView view)
    {
        foreach (var candidate in new[] { "name", "key", "id" })
        {
            var actual = view.Actual(candidate);
            if (actual is not null)
            {
                var value = view.Value(actual);
                if (value.Length > 0)
                {
                    view.KeyAttributeName = actual;
                    view.KeyValue = value;
                    break;
                }
            }
        }
    }

    private static void CollectConnectionHint(HintBuckets buckets, ElementView view)
    {
        var connectionAttr = view.Actual("connectionstring");
        if (connectionAttr is not null)
        {
            AddAttributeHint(buckets, "connection_strings", view, connectionAttr, view.Value(connectionAttr));
        }
    }

    private static void CollectEndpointHints(HintBuckets buckets, ElementView view)
    {
        foreach (var candidate in new[] { "address", "endpoint", "url", "uri", "baseaddress", "serviceurl" })
        {
            var attrName = view.Actual(candidate);
            if (attrName is null)
            {
                continue;
            }

            var value = view.Value(attrName);
            if (LooksLikeEndpoint(value))
            {
                AddAttributeHint(buckets, "service_endpoints", view, attrName, value);
            }
        }

        var valueAttribute = view.Actual("value");
        if (valueAttribute is not null && !string.IsNullOrEmpty(view.KeyValue) && ContainsEndpointKeyword(view.KeyValue))
        {
            var value = view.Value(valueAttribute);
            if (LooksLikeEndpoint(value))
            {
                AddAttributeHint(buckets, "service_endpoints", view, valueAttribute, value);
            }
        }
    }

    private static void CollectFeatureHint(HintBuckets buckets, ElementView view)
    {
        string? featureValue = null;
        string? featureAttrName = null;
        if (!string.IsNullOrEmpty(view.KeyValue) && ContainsFeatureKeyword(view.KeyValue))
        {
            (featureValue, featureAttrName) = FirstFeatureValue(view);
        }
        else if (ContainsFeatureKeyword(view.ElementName))
        {
            foreach (var candidate in new[] { "name", "key" })
            {
                var actual = view.Actual(candidate);
                if (actual is not null && string.IsNullOrEmpty(view.KeyValue))
                {
                    view.KeyAttributeName = actual;
                    view.KeyValue = view.Attributes[actual];
                    break;
                }
            }

            (featureValue, featureAttrName) = FirstFeatureValue(view);
        }

        if (!string.IsNullOrEmpty(featureValue))
        {
            AddAttributeHint(buckets, "feature_flags", view, featureAttrName ?? "value", featureValue);
        }
    }

    // The first non-empty value among the feature-bearing attributes, with the attribute name it came from.
    private static (string? Value, string? AttributeName) FirstFeatureValue(ElementView view)
    {
        foreach (var candidate in new[] { "value", "enabled", "isenabled", "defaultvalue" })
        {
            var attrName = view.Actual(candidate);
            if (attrName is not null)
            {
                var candidateValue = view.Value(attrName);
                if (candidateValue.Length > 0)
                {
                    return (candidateValue, attrName);
                }
            }
        }

        return (null, null);
    }

    private static void AddAttributeHint(HintBuckets buckets, string category, ElementView view, string attributeName, string value)
    {
        var cleaned = EngineText.Strip(value);
        if (cleaned.Length == 0)
        {
            return;
        }

        var digest = Sha256Hex(cleaned);
        var dedupeKey = (
            digest,
            EngineText.Lower(EngineText.Strip(view.KeyValue ?? string.Empty)),
            EngineText.Lower(attributeName),
            EngineText.Lower(view.ElementName));
        var bucket = buckets.Seen[category];
        if (bucket.Contains(dedupeKey))
        {
            return;
        }

        var entry = new JsonObject()
        {
            ["element"] = view.ElementName,
            ["attribute"] = attributeName,
            ["hash"] = digest,
            ["length"] = CodePointCount(cleaned),
        };
        if (!string.IsNullOrEmpty(view.KeyValue))
        {
            entry["key"] = view.KeyValue;
        }

        if (!string.IsNullOrEmpty(view.KeyAttributeName))
        {
            entry["key_attribute"] = view.KeyAttributeName;
        }

        buckets.Hints[category].Add(entry);
        bucket.Add(dedupeKey);
    }

    internal static bool LooksLikeEndpoint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var cleaned = EngineText.Strip(value);
        if (cleaned.Length == 0)
        {
            return false;
        }

        var lowered = EngineText.Lower(cleaned);
        if (cleaned.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        if (cleaned.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return lowered.StartsWith("net.tcp://", StringComparison.Ordinal) || lowered.StartsWith("sb://", StringComparison.Ordinal);
    }

    private static bool ContainsFeatureKeyword(string text)
    {
        var lowered = EngineText.Lower(text);
        return lowered.Contains("feature", StringComparison.Ordinal)
            || lowered.Contains("flag", StringComparison.Ordinal)
            || lowered.Contains("toggle", StringComparison.Ordinal);
    }

    private static bool ContainsEndpointKeyword(string text)
    {
        var lowered = EngineText.Lower(text);
        foreach (var keyword in new[] { "endpoint", "serviceurl", "baseaddress", "callback", "apiurl", "address" })
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractMsbuildMetadata(XElement root, JsonObject metadata, string extension)
    {
        if (!LooksLikeMsbuild(extension, metadata))
        {
            return;
        }

        metadata["msbuild_detected"] = true;
        metadata["msbuild_kind"] = ClassifyMsbuildKind(extension);

        var attrLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in ElementAttributes(root))
        {
            var cleaned = EngineText.Strip(value);
            if (cleaned.Length > 0)
            {
                attrLookup[EngineText.Lower(name)] = cleaned;
            }
        }

        if (attrLookup.TryGetValue("defaulttargets", out var defaultTargets))
        {
            var targets = defaultTargets.Split(';').Select(EngineText.Strip).Where(token => token.Length > 0).ToList();
            if (targets.Count > 0)
            {
                metadata["msbuild_default_targets"] = JsonNodes.Strings(targets);
            }
        }

        if (attrLookup.TryGetValue("toolsversion", out var toolsVersion))
        {
            metadata["msbuild_tools_version"] = toolsVersion;
        }

        if (attrLookup.TryGetValue("sdk", out var sdk))
        {
            metadata["msbuild_sdk"] = sdk;
        }

        var (targetNames, importHints) = CollectMsbuildElements(root);
        if (targetNames.Count > 0)
        {
            metadata["msbuild_targets"] = JsonNodes.Strings(targetNames);
        }

        if (importHints.Count > 0)
        {
            metadata["msbuild_import_hints"] = JsonNodes.Array(importHints);
        }
    }

    private static (List<string> Targets, List<JsonObject> Imports) CollectMsbuildElements(XElement root)
    {
        var targetNames = new List<string>();
        var seenTargetNames = new HashSet<string>(StringComparer.Ordinal);
        var importHints = new List<JsonObject>();
        var seenImports = new HashSet<(string, string)>();

        foreach (var element in root.DescendantsAndSelf())
        {
            var localName = element.Name.LocalName;
            if (string.Equals(localName, "Target", StringComparison.Ordinal))
            {
                var name = element.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    var cleanedName = EngineText.Strip(name);
                    if (cleanedName.Length > 0 && seenTargetNames.Add(EngineText.Lower(cleanedName)) && targetNames.Count < 10)
                    {
                        targetNames.Add(cleanedName);
                    }
                }
            }

            if (string.Equals(localName, "Import", StringComparison.Ordinal))
            {
                CollectImportHint(element, importHints, seenImports);
            }
        }

        return (targetNames, importHints);
    }

    private static void CollectImportHint(XElement element, List<JsonObject> importHints, HashSet<(string, string)> seenImports)
    {
        foreach (var attribute in new[] { "Project", "Sdk" })
        {
            var rawValue = element.Attribute(attribute)?.Value;
            if (string.IsNullOrEmpty(rawValue))
            {
                continue;
            }

            var cleanedValue = EngineText.Strip(rawValue);
            if (cleanedValue.Length == 0)
            {
                continue;
            }

            var digest = Sha256Hex(cleanedValue);
            if (!seenImports.Add((EngineText.Lower(attribute), digest)))
            {
                continue;
            }

            var entry = new JsonObject()
            {
                ["attribute"] = attribute,
                ["hash"] = digest,
                ["length"] = CodePointCount(cleanedValue),
            };
            var conditionValue = element.Attribute("Condition")?.Value;
            if (!string.IsNullOrEmpty(conditionValue))
            {
                var cleanedCondition = EngineText.Strip(conditionValue);
                if (cleanedCondition.Length > 0)
                {
                    entry["condition_hash"] = Sha256Hex(cleanedCondition);
                    entry["condition_length"] = CodePointCount(cleanedCondition);
                }
            }

            importHints.Add(entry);
            break;
        }
    }
}
