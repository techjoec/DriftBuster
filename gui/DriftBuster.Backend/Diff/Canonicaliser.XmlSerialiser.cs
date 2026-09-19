using System.Text;
using System.Xml.Linq;

using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

public static partial class Canonicaliser
{
    private static readonly Comparer<string> CodePointOrder = Comparer<string>.Create(PathText.CompareCodePoints);

    /// <summary>
    /// Written names: <c>&lt;name</c>, the element's namespace declarations, attributes sorted by expanded name, then <c> /&gt;</c> when
    /// empty, else <c>&gt;</c>, text, children and end tag; each node followed by its tail. Comments are unescaped. Each start tag,
    /// comment and end tag of an element with children starts a new line indented two spaces per depth, so a line diff shows the
    /// drifted element. Explicit stack.
    /// </summary>
    private static string SerialiseXml(XElement root)
    {
        var builder = new StringBuilder();
        var stack = new Stack<(XNode Node, bool Closing, int Depth)>();
        stack.Push((root, false, 0));
        while (stack.Count > 0)
        {
            var (node, closing, depth) = stack.Pop();
            switch (node)
            {
                case XComment comment:
                    StartLine(builder, depth);
                    builder.Append("<!--").Append(CollapseWhitespace(comment.Value)).Append("-->");
                    AppendTail(builder, comment);
                    break;
                case XElement element when closing:
                    if (element.Nodes().Any(child => child is not XText))
                    {
                        StartLine(builder, depth);
                    }

                    builder.Append("</").Append(WrittenName(element)).Append('>');
                    AppendTail(builder, element);
                    break;
                case XElement element:
                    StartLine(builder, depth);
                    OpenElement(builder, stack, element, depth);
                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }

    private static void StartLine(StringBuilder builder, int depth)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n').Append(' ', depth * 2);
        }
    }

    private static void OpenElement(StringBuilder builder, Stack<(XNode Node, bool Closing, int Depth)> stack, XElement element, int depth)
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

        // Keys are unique expanded names ("{uri}local" or "local").
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
        stack.Push((element, true, depth));
        for (var index = children.Count - 1; index >= 0; index--)
        {
            stack.Push((children[index], false, depth + 1));
        }
    }

    private static string WrittenName(XObject node) => node.Annotation<XmlWrittenName>()!.QualifiedName;

    // Only whitespace-only values collapse; others keep their padding.
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

    // & < > only.
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

    // & < > " plus CR, LF and tab as numeric references (&#09; keeps its leading zero).
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
