using System.Text;
using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// The full tree for the diff canonicaliser: elements, attributes, character data, comments inside the document element, and
/// written names so namespace prefixes are kept.
/// </summary>
internal sealed partial class DefusedXmlParser
{
    // The start tag being stored records its namespace declarations here during a canonical parse.
    private List<XmlNamespaceDeclaration>? _declarations;

    // Character data since the last markup, added to the open element as one text node when the next markup starts.
    private readonly StringBuilder _pendingText = new();

    /// <summary>
    /// The document element with its content, or null when the parse fails. Character data (line ends as LF) becomes
    /// <see cref="XText"/> (a processing instruction may split it into adjacent nodes); comments inside the root become
    /// <see cref="XComment"/>; every element and attribute carries an <see cref="XmlWrittenName"/>.
    /// </summary>
    /// <remarks>Acceptance is the defused parser's: a DOCTYPE that declares entities is refused, so no entity is ever resolved.</remarks>
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
