using System.Text;
using System.Xml.Linq;

using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

public static partial class Canonicaliser
{
    private static readonly Comparer<string> CodePointOrder = Comparer<string>.Create(PathText.CompareCodePoints);

    /// <summary>
    /// <c>_normalise</c> then <c>ET.tostring(root, encoding="unicode")</c> with written names: <c>&lt;name</c>, the
    /// element's namespace declarations, attributes sorted by expanded name, then <c> /&gt;</c> when there is neither
    /// text nor a child, otherwise <c>&gt;</c>, text, children and the end tag; every node is followed by its tail.
    /// Comments are written <c>&lt;!--text--&gt;</c> without escaping. Walked on an explicit stack.
    /// </summary>
    private static string SerialiseXml(XElement root)
    {
        var builder = new StringBuilder();
        var stack = new Stack<(XNode Node, bool Closing)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, closing) = stack.Pop();
            switch (node)
            {
                case XComment comment:
                    builder.Append("<!--").Append(CollapseWhitespace(comment.Value)).Append("-->");
                    AppendTail(builder, comment);
                    break;
                case XElement element when closing:
                    builder.Append("</").Append(WrittenName(element)).Append('>');
                    AppendTail(builder, element);
                    break;
                case XElement element:
                    OpenElement(builder, stack, element);
                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }

    private static void OpenElement(StringBuilder builder, Stack<(XNode Node, bool Closing)> stack, XElement element)
    {
        var written = element.Annotation<XmlWrittenName>()!;
        builder.Append('<').Append(written.QualifiedName);
        foreach (var declaration in written.Declarations.OrderBy(declaration => declaration.Prefix, CodePointOrder))
        {
            builder.Append(" xmlns");
            if (declaration.Prefix.Length > 0)
            {
                builder.Append(':').Append(declaration.Prefix);
            }

            builder.Append("=\"");
            AppendEscapedAttribute(builder, declaration.Uri).Append('"');
        }

        // sorted(element.attrib.items()): keys are unique expanded names ("{uri}local" or "local").
        foreach (var attribute in element.Attributes().OrderBy(attribute => attribute.Name.ToString(), CodePointOrder))
        {
            builder.Append(' ').Append(WrittenName(attribute)).Append("=\"");
            AppendEscapedAttribute(builder, CollapseWhitespace(attribute.Value)).Append('"');
        }

        var text = CollapseWhitespace(string.Concat(element.Nodes().TakeWhile(child => child is XText).Cast<XText>().Select(child => child.Value)));
        var children = element.Nodes().Where(child => child is not XText).ToList();
        if (text.Length == 0 && children.Count == 0)
        {
            builder.Append(" />");
            AppendTail(builder, element);
            return;
        }

        builder.Append('>');
        AppendEscapedText(builder, text);
        stack.Push((element, true));
        for (var index = children.Count - 1; index >= 0; index--)
        {
            stack.Push((children[index], false));
        }
    }

    private static string WrittenName(XObject node) => node.Annotation<XmlWrittenName>()!.QualifiedName;

    // Only values that are entirely Python white space collapse; any other value keeps its padding.
    private static string CollapseWhitespace(string value) => EngineText.Strip(value).Length == 0 ? string.Empty : value;

    // The tail: the character data between this node and the next non-text sibling.
    private static void AppendTail(StringBuilder builder, XNode node)
    {
        var tail = new StringBuilder();
        for (var next = node.NextNode; next is XText text; next = next.NextNode)
        {
            tail.Append(text.Value);
        }

        AppendEscapedText(builder, CollapseWhitespace(tail.ToString()));
    }

    // _escape_cdata: & < > only.
    private static void AppendEscapedText(StringBuilder builder, string text)
    {
        foreach (var ch in text)
        {
            _ = ch switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                _ => builder.Append(ch),
            };
        }
    }

    // _escape_attrib: & < > " plus CR, LF and tab as numeric references (&#09; keeps its leading zero).
    private static StringBuilder AppendEscapedAttribute(StringBuilder builder, string text)
    {
        foreach (var ch in text)
        {
            _ = ch switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                '\r' => builder.Append("&#13;"),
                '\n' => builder.Append("&#10;"),
                '\t' => builder.Append("&#09;"),
                _ => builder.Append(ch),
            };
        }

        return builder;
    }
}
