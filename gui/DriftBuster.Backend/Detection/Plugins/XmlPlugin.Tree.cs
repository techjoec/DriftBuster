using System.Xml.Linq;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Tree parsing and the walks over it. Both the tree and the well-formedness probe come from
/// <see cref="DefusedXmlParser"/>, which accepts exactly what <c>defusedxml.ElementTree.fromstring</c> accepts. The
/// plugin's own guard refuses the tree parse for any payload carrying a DOCTYPE or ENTITY declaration, and a payload
/// longer than the cap is never parsed. The tree holds elements and attributes only, as ElementTree's default parser
/// leaves comments and processing instructions out. The no-defusedxml fallback branch alone inserts comments, whose
/// <c>tag</c> is a function in Python: the resx and MSBuild walks then raise <c>TypeError</c> on reaching one.
/// </summary>
public sealed partial class XmlPlugin
{
    /// <summary>
    /// Test seam choosing the parser: false takes the fallback branch, where the tree comes from <c>ET.fromstring(text, parser=XMLParser(target=TreeBuilder(insert_comments=True)))</c>.
    /// </summary>
    internal bool DefusedAvailable { get; set; } = true;

    /// <summary>
    /// Test seam for the defused parse: called with the stripped text, it returns
    /// the root element or null where the parse fails.
    /// </summary>
    internal Func<string, XElement?> DefusedFromString { get; set; } = static text => DefusedXmlParser.ParseTree(text);

    /// <summary>
    /// Test seam for the parse on the fallback branch: called with the text and the parser options (comments
    /// inserted), it returns the root element or null where the parse fails.
    /// </summary>
    internal Func<string, FallbackParserOptions, XElement?> FallbackFromString { get; set; } =
        (text, parser) => DefusedXmlParser.ParseTree(text, parser.InsertComments);

    /// <summary>The fallback branch's <c>XMLParser(target=TreeBuilder(insert_comments=True))</c>.</summary>
    internal sealed record FallbackParserOptions(bool InsertComments);

    private XElement? ParseTree(string text)
    {
        var stripped = EngineText.StripStart(text);
        if (stripped.Length == 0 || CodePointCount(stripped) > MaxSafeParseChars)
        {
            return null;
        }

        if (FindDoctypeName(stripped) is not null || HasEntityDeclaration(stripped))
        {
            return null;
        }

        return DefusedAvailable ? DefusedFromString(stripped) : FallbackFromString(stripped, new FallbackParserOptions(InsertComments: true));
    }

    // DEFUSED_ET.fromstring(sample_text) succeeding.
    private static bool IsWellFormed(string sampleText) => DefusedXmlParser.IsWellFormed(sampleText);

    /// <summary>
    /// <c>root.iter()</c> read through <c>element.tag</c>: elements in document order, root first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A comment node (fallback branch only) is reached: Python's <c>"}" in element.tag</c> raises
    /// <c>TypeError</c> on the <c>Comment</c> factory function, and the plugin does not catch it.
    /// </exception>
    private static IEnumerable<XElement> IterTree(XElement root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case XElement element:
                    yield return element;
                    break;
                case XComment:
                    throw new InvalidOperationException("argument of type 'function' is not iterable");
            }
        }
    }

    private static void ExtractResxKeys(XElement root, OrderedDictionary<string, object?> metadata)
    {
        if (!string.Equals(EngineText.Lower(root.Name.LocalName), "root", StringComparison.Ordinal))
        {
            return;
        }

        string? namespaceHint = null;
        if (metadata.TryGetValue("root_namespace", out var rootNamespace) && rootNamespace is string rootNamespaceText)
        {
            namespaceHint = rootNamespaceText;
        }
        else if (metadata.TryGetValue("namespaces", out var namespaces) && namespaces is OrderedDictionary<string, object?> namespaceMap)
        {
            namespaceHint = namespaceMap.TryGetValue("default", out var defaultNs) ? defaultNs as string : null;
        }

        if (string.IsNullOrEmpty(namespaceHint) || !HasResxSchema(namespaceHint))
        {
            return;
        }

        var resourceKeys = new List<string>();
        foreach (var element in IterTree(root))
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
            metadata["resource_keys"] = resourceKeys;
            metadata.TryAdd("resource_keys_preview", string.Join(", ", resourceKeys.Take(3)));
        }
    }

    /// <summary>One element's attributes as Python sees them plus the <c>lower_to_actual</c> lookup (last duplicate wins).</summary>
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

        /// <summary><c>lower_to_actual.get(candidate)</c>.</summary>
        public string? Actual(string candidate) => LowerToActual.TryGetValue(candidate, out var actual) ? actual : null;

        /// <summary><c>attributes.get(name, "")</c>.</summary>
        public string Value(string name) => Attributes.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private sealed class HintBuckets
    {
        private static readonly string[] Categories = ["connection_strings", "service_endpoints", "feature_flags"];

        public OrderedDictionary<string, List<OrderedDictionary<string, object?>>> Hints { get; } = Create();

        public Dictionary<string, HashSet<(string, string, string, string)>> Seen { get; } =
            Categories.ToDictionary(category => category, _ => new HashSet<(string, string, string, string)>(), StringComparer.Ordinal);

        private static OrderedDictionary<string, List<OrderedDictionary<string, object?>>> Create()
        {
            var hints = new OrderedDictionary<string, List<OrderedDictionary<string, object?>>>(StringComparer.Ordinal);
            foreach (var category in Categories)
            {
                hints[category] = [];
            }

            return hints;
        }
    }

    private static void ExtractAttributeHints(XElement root, OrderedDictionary<string, object?> metadata)
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

        var filtered = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (category, entries) in buckets.Hints)
        {
            if (entries.Count > 0)
            {
                filtered[category] = entries;
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

        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
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

    private static void ExtractMsbuildMetadata(XElement root, OrderedDictionary<string, object?> metadata, string extension)
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
                metadata["msbuild_default_targets"] = targets;
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
            metadata["msbuild_targets"] = targetNames;
        }

        if (importHints.Count > 0)
        {
            metadata["msbuild_import_hints"] = importHints;
        }
    }

    private static (List<string> Targets, List<OrderedDictionary<string, object?>> Imports) CollectMsbuildElements(XElement root)
    {
        var targetNames = new List<string>();
        var seenTargetNames = new HashSet<string>(StringComparer.Ordinal);
        var importHints = new List<OrderedDictionary<string, object?>>();
        var seenImports = new HashSet<(string, string)>();

        foreach (var element in IterTree(root))
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

    private static void CollectImportHint(XElement element, List<OrderedDictionary<string, object?>> importHints, HashSet<(string, string)> seenImports)
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

            var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
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
