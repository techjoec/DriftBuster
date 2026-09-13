using System.Text;
using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// The tree <c>ET.fromstring(text, XMLParser(target=TreeBuilder(insert_comments=True)))</c> builds, for the diff
/// canonicaliser: elements with their attributes, character data, comments inside the document element, and written
/// names for keeping namespace prefixes.
/// </summary>
internal sealed partial class DefusedXmlParser
{
    // The start tag being stored records its namespace declarations here during a canonical parse.
    private List<XmlNamespaceDeclaration>? _declarations;

    // Character data since the last markup, added to the open element as one text node when the next markup starts.
    private readonly StringBuilder _pendingText = new();

    /// <summary>
    /// The document element with its full content, or null where the parse fails. Character data (text, character
    /// references, predefined entities and CDATA sections, line ends normalised to LF) becomes <see cref="XText"/> nodes;
    /// a processing instruction adds nothing but may split the text around it into two adjacent nodes, which a reader
    /// joins as TreeBuilder does. Comments inside the document element become <see cref="XComment"/> nodes. Every
    /// element and attribute carries an <see cref="XmlWrittenName"/> annotation.
    /// </summary>
    /// <remarks>
    /// The acceptance rules are the defused parser's. Plain ElementTree also accepts a DOCTYPE whose processed internal
    /// subset declares entities (and expands references to them); this parse refuses such a document, so no entity is
    /// ever resolved.
    /// </remarks>
    public static XElement? ParseCanonicalTree(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return new DefusedXmlParser(text, buildTree: true, canonical: true).Parse();
        }
        catch (NotWellFormedException)
        {
            return null;
        }
    }

    private static string NormaliseLineEnds(string data)
        => data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private void AnnotateCanonical(XObject node, string qualifiedName, IReadOnlyList<XmlNamespaceDeclaration>? declarations)
    {
        if (_canonical)
        {
            node.AddAnnotation(new XmlWrittenName(qualifiedName, declarations ?? []));
        }
    }

    private void AppendCanonicalText(Stack<OpenElement> open, int start, int end)
    {
        if (_canonical && end > start && open.Count > 0)
        {
            _pendingText.Append(NormaliseLineEnds(_text[start..end]));
        }
    }

    // An XText node is added, never a string, so XContainer does not re-concatenate a growing text value per chunk.
    private void FlushCanonicalText(Stack<OpenElement> open)
    {
        if (_pendingText.Length == 0)
        {
            return;
        }

        open.Peek().Node!.Add(new XText(_pendingText.ToString()));
        _pendingText.Clear();
    }

    // A reference already validated by ScanReference: a character reference contributes its code point unchanged (a
    // &#13; stays CR), a predefined entity its character.
    private void AppendCanonicalReference(Stack<OpenElement> open, int start, Range nameSpan)
    {
        if (!_canonical || open.Count == 0)
        {
            return;
        }

        string value;
        if (_text[start + 1] == '#')
        {
            ScanCharacterReference(start + 2, _text.Length, out var codePoint);
            value = char.ConvertFromUtf32(codePoint);
        }
        else
        {
            value = _text[nameSpan] switch
            {
                "lt" => "<",
                "gt" => ">",
                "amp" => "&",
                "quot" => "\"",
                _ => "'",
            };
        }

        _pendingText.Append(value);
    }
}
