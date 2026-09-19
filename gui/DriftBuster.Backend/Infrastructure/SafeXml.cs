using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// XML reads for scanned files: namespace-aware <see cref="XmlReader"/> with the DTD skipped (<see cref="DtdProcessing.Ignore"/>),
/// no resolver and no entity expansion, so nothing external is fetched and a reference to a DTD-declared entity makes the document
/// not well formed instead of expanding it. One leading U+FEFF is ignored.
/// </summary>
internal static class SafeXml
{
    private const string XmlnsUri = "http://www.w3.org/2000/xmlns/";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>True when the text reads to the end without an XML error.</summary>
    public static bool IsWellFormed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var reader = Create(text);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The document element, or null when the text is not well formed. Each run of character data becomes one <see cref="XText"/>
    /// (line ends as LF); namespace declarations stay on their element as <c>xmlns</c> attributes, so written prefixes survive.
    /// </summary>
    /// <param name="text">The document text.</param>
    /// <param name="keepComments">Keep comments inside the document element as <see cref="XComment"/> nodes.</param>
    public static XElement? TryLoadRoot(string text, bool keepComments = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var reader = Create(text);
            return Build(reader, keepComments);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static XmlReader Create(string text)
        => XmlReader.Create(new StringReader(text.StartsWith('﻿') ? text[1..] : text), ReaderSettings);

    // Built node by node rather than with XDocument.Load, which appends each text piece to the previous one and goes quadratic on a
    // run split by many processing instructions or references.
    private static XElement? Build(XmlReader reader, bool keepComments)
    {
        XElement? root = null;
        var open = new Stack<XElement>();
        var text = new StringBuilder();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    Flush(open, text);
                    var element = ReadElement(reader);
                    if (open.TryPeek(out var parent))
                    {
                        parent.Add(element);
                    }
                    else
                    {
                        root = element;
                    }

                    if (!reader.IsEmptyElement)
                    {
                        open.Push(element);
                    }

                    break;
                case XmlNodeType.EndElement:
                    Flush(open, text);
                    open.Pop();
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (open.Count > 0)
                    {
                        text.Append(reader.Value);
                    }

                    break;
                case XmlNodeType.Comment when keepComments && open.Count > 0:
                    Flush(open, text);
                    open.Peek().Add(new XComment(reader.Value));
                    break;
                default:
                    break;
            }
        }

        return root;
    }

    private static XElement ReadElement(XmlReader reader)
    {
        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
        while (reader.MoveToNextAttribute())
        {
            var name = string.Equals(reader.NamespaceURI, XmlnsUri, StringComparison.Ordinal)
                ? (reader.Prefix.Length == 0 ? XName.Get("xmlns") : XNamespace.Xmlns + reader.LocalName)
                : XName.Get(reader.LocalName, reader.NamespaceURI);
            element.Add(new XAttribute(name, reader.Value));
        }

        reader.MoveToElement();
        return element;
    }

    private static void Flush(Stack<XElement> open, StringBuilder text)
    {
        if (text.Length > 0 && open.Count > 0)
        {
            open.Peek().Add(new XText(text.ToString()));
        }

        text.Clear();
    }
}
