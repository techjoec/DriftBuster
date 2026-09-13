using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Start tags: attribute scanning, namespace declarations and defaults, name expansion and the tree node.</summary>
internal sealed partial class DefusedXmlParser
{
    /// <summary>An attribute as written: its qualified name and the raw value between the quotes.</summary>
    private readonly record struct RawAttribute(string Name, int ValueStart, int ValueEnd);

    /// <summary>An attribute after normalisation, its prefix still to resolve when it has one.</summary>
    private readonly record struct PendingAttribute(string Name, string Value);

    // In-scope namespace bindings ("" is the default namespace); xml is bound from the start.
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal) { ["xml"] = XmlNamespace };

    // Bindings to restore when elements close: the prefix and the URI it had before (null when unbound).
    private readonly List<(string Prefix, string? Previous)> _bindingHistory = [];

    private OpenElement ReadStartTag(XElement? parent, out bool empty)
    {
        var nameStart = _pos + 1;
        var index = ScanQualifiedName(nameStart);
        var qualifiedName = _text[nameStart..index];
        var raw = new List<RawAttribute>();
        while (true)
        {
            var ch = Require(index);
            if (ch is '>' or '/')
            {
                empty = ch == '/';
                _pos = !empty ? index + 1 : Require(index + 1) == '>' ? index + 2 : throw Fail();
                break;
            }

            if (!IsSpace(ch))
            {
                throw Fail();
            }

            while (IsSpace(Require(index)))
            {
                index++;
            }

            if (_text[index] is '>' or '/')
            {
                continue;
            }

            index = ScanAttribute(index, raw);
            if (!(IsSpace(Require(index)) || _text[index] is '>' or '/'))
            {
                throw Fail();
            }
        }

        var mark = _bindingHistory.Count;
        var node = StoreAttributes(qualifiedName, raw);
        parent?.Add(node);
        return new OpenElement(qualifiedName, mark, node);
    }

    // A name start, then name characters with at most one colon, which must be followed by a name start.
    private int ScanQualifiedName(int index)
    {
        if (!IsNameStart(Require(index)))
        {
            throw Fail();
        }

        var hadColon = false;
        index++;
        while (true)
        {
            var ch = Require(index);
            if (IsNameChar(ch))
            {
                index++;
            }
            else if (ch == ':' && !hadColon && IsNameStart(Require(index + 1)))
            {
                hadColon = true;
                index += 2;
            }
            else if (ch == ':')
            {
                throw Fail();
            }
            else
            {
                return index;
            }
        }
    }

    // name S* '=' S* quoted value, where the value holds no '<' and only well-formed references.
    private int ScanAttribute(int index, List<RawAttribute> raw)
    {
        var nameStart = index;
        index = ScanQualifiedName(index);
        var nameEnd = index;
        while (IsSpace(Require(index)))
        {
            index++;
        }

        if (_text[index] != '=')
        {
            throw Fail();
        }

        do
        {
            index++;
        }
        while (IsSpace(Require(index)));

        var quote = _text[index];
        if (quote is not ('"' or '\''))
        {
            throw Fail();
        }

        var valueStart = ++index;
        while (Require(index) != quote)
        {
            index = _text[index] switch
            {
                '<' => throw Fail(),
                '&' => ScanReference(index, _text.Length, out _, out _),
                _ => index + CharWidth(index),
            };
        }

        raw.Add(new RawAttribute(_text[nameStart..nameEnd], valueStart, index));
        return index + 1;
    }

    /// <summary>
    /// expat's storeAtts: attributes may not repeat; values are normalised by their declared type; namespace
    /// declarations bind as they are met, then the element's declared defaults that were not specified apply
    /// (namespace declarations first bound, others appended); prefixed names then expand, and may not repeat expanded.
    /// </summary>
    private XElement? StoreAttributes(string qualifiedName, List<RawAttribute> raw)
    {
        _attributeDefaults.TryGetValue(qualifiedName, out var defaults);
        var specified = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<PendingAttribute>();
        foreach (var attribute in raw)
        {
            if (!specified.Add(attribute.Name))
            {
                throw Fail();
            }

            var isCdata = defaults?.Find(entry => string.Equals(entry.Name, attribute.Name, StringComparison.Ordinal))?.IsCdata ?? true;
            AddAttribute(pending, attribute.Name, NormalizeAttributeValue(attribute.ValueStart, attribute.ValueEnd, isCdata));
        }

        foreach (var entry in defaults ?? [])
        {
            if (entry.Value is not null && !specified.Contains(entry.Name))
            {
                AddAttribute(pending, entry.Name, entry.Value);
            }
        }

        var expanded = ExpandAttributes(pending);
        var elementName = ExpandName(qualifiedName, useDefault: true);
        if (!_buildTree)
        {
            return null;
        }

        var node = new XElement(elementName);
        foreach (var (name, value) in expanded)
        {
            node.Add(new XAttribute(name, value));
        }

        return node;
    }

    private void AddAttribute(List<PendingAttribute> pending, string name, string value)
    {
        if (string.Equals(name, "xmlns", StringComparison.Ordinal))
        {
            AddBinding(string.Empty, value);
        }
        else if (name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            AddBinding(name[6..], value);
        }
        else
        {
            pending.Add(new PendingAttribute(name, value));
        }
    }

    // Prefixed attribute names resolve against the element's bindings; unprefixed ones are in no namespace.
    private List<(XName Name, string Value)> ExpandAttributes(List<PendingAttribute> pending)
    {
        var seen = new HashSet<XName>();
        var expanded = new List<(XName Name, string Value)>(pending.Count);
        foreach (var attribute in pending)
        {
            var name = ExpandName(attribute.Name, useDefault: false);
            if (attribute.Name.Contains(':', StringComparison.Ordinal) && !seen.Add(name))
            {
                throw Fail();
            }

            expanded.Add((name, attribute.Value));
        }

        return expanded;
    }

    private XName ExpandName(string qualifiedName, bool useDefault)
    {
        var colon = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            var uri = useDefault && _bindings.TryGetValue(string.Empty, out var defaultUri) ? defaultUri : string.Empty;
            return XName.Get(qualifiedName, uri);
        }

        if (!_bindings.TryGetValue(qualifiedName[..colon], out var prefixUri))
        {
            throw Fail();
        }

        return XName.Get(qualifiedName[(colon + 1)..], prefixUri);
    }

    /// <summary>
    /// A namespace declaration: a prefix may not be bound to the empty name, <c>xmlns</c> may not be bound, <c>xml</c>
    /// only to its own namespace, no other prefix (nor the default) to the xml or xmlns namespace, and no namespace name
    /// may contain the parser's <c>}</c> separator. An empty default declaration unbinds the default namespace.
    /// </summary>
    private void AddBinding(string prefix, string uri)
    {
        var isXml = string.Equals(uri, XmlNamespace, StringComparison.Ordinal);
        if ((prefix.Length > 0 && uri.Length == 0)
            || string.Equals(prefix, "xmlns", StringComparison.Ordinal)
            || string.Equals(prefix, "xml", StringComparison.Ordinal) != isXml
            || string.Equals(uri, XmlnsNamespace, StringComparison.Ordinal)
            || uri.Contains('}', StringComparison.Ordinal))
        {
            throw Fail();
        }

        _bindingHistory.Add((prefix, _bindings.TryGetValue(prefix, out var previous) ? previous : null));
        if (uri.Length == 0)
        {
            _bindings.Remove(prefix);
        }
        else
        {
            _bindings[prefix] = uri;
        }
    }

    private void PopBindings(int mark)
    {
        for (var index = _bindingHistory.Count - 1; index >= mark; index--)
        {
            var (prefix, previous) = _bindingHistory[index];
            if (previous is null)
            {
                _bindings.Remove(prefix);
            }
            else
            {
                _bindings[prefix] = previous;
            }
        }

        _bindingHistory.RemoveRange(mark, _bindingHistory.Count - mark);
    }
}
