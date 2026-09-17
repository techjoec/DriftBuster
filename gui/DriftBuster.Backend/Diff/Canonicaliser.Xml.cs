using System.Text;
using System.Xml.Linq;

using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

public static partial class Canonicaliser
{
    private const string DoctypeKeyword = "<!DOCTYPE";

    /// <summary>
    /// <c>canonicalise_xml</c>: the XML declaration and DOCTYPE captured verbatim, the rest parsed (comments kept) and
    /// re-serialised on one line with attributes sorted, white-space-only attribute values, text, tails and comments
    /// collapsed to empty, and the captured prolog joined in front with LF. A payload that does not parse goes through
    /// <see cref="CanonicaliseText"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Namespace prefixes are kept as written. ElementTree serialises every namespaced name with a prefix
    /// of its own choosing (<c>ns0</c>, <c>ns1</c>, ... in first-use order, or a well-known prefix such as <c>xsi</c>),
    /// declares every used namespace once on the root sorted by that prefix and drops unused declarations; a QName
    /// inside an attribute value then points at a prefix that no longer exists. The canonicaliser writes element and attribute
    /// names with their original prefixes and keeps each <c>xmlns</c>/<c>xmlns:p</c> declaration on the element that
    /// made it, sorted by prefix and before the attributes. Attributes are ordered by <c>{uri}local</c>, so
    /// renaming prefixes and moving the declarations maps one output onto the other.
    /// </para>
    /// <para>
    /// A document whose DOCTYPE reaches the parser (one not at the start, or one the bracket scan could not close) and
    /// declares entities is refused, so it canonicalises as text; ElementTree would expand the entities. Documents
    /// nested deeper than a recursion limit and text holding unpaired surrogates would raise
    /// (<c>RecursionError</c>, <c>UnicodeEncodeError</c>); the canonicaliser normalises and serialises on an explicit stack and
    /// treats a surrogate as a parse failure.
    /// </para>
    /// </remarks>
    public static string CanonicaliseXml(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        payload = payload.TrimStart(Bom);
        var xmlDeclaration = string.Empty;
        var doctype = string.Empty;
        var working = EngineText.StripStart(payload);

        var declarationEnd = MatchXmlDeclaration(working);
        if (declarationEnd > 0)
        {
            xmlDeclaration = working[..declarationEnd];
            working = EngineText.StripStart(working[declarationEnd..]);
        }

        if (UpperStartsWithDoctype(working))
        {
            var end = DoctypeEnd(working);
            if (end > 0)
            {
                doctype = working[..end];
                working = EngineText.StripStart(working[end..]);
            }
            else
            {
                // A DOCTYPE the scan cannot close: parse the original payload with no captured prolog.
                working = payload;
                xmlDeclaration = string.Empty;
            }
        }

        var root = DefusedXmlParser.ParseCanonicalTree(working);
        if (root is null)
        {
            return CanonicaliseText(payload);
        }

        var serialised = SerialiseXml(root);
        var prolog = string.Join("\n", new[] { xmlDeclaration, doctype }.Where(part => part.Length > 0));
        return prolog.Length > 0 ? prolog + "\n" + serialised : serialised;
    }

    // re.compile(r"<\?xml[^>]*\?>", re.IGNORECASE).match(text): "<?xml" in any ASCII case (no other code point folds to
    // x, m or l), then the first '>' must directly follow a '?' that is not part of "<?xml". Returns the match end or 0.
    private static int MatchXmlDeclaration(string text)
    {
        if (text.Length < 7 || text[0] != '<' || text[1] != '?' || (text[2] | 0x20) != 'x' || (text[3] | 0x20) != 'm' || (text[4] | 0x20) != 'l')
        {
            return 0;
        }

        var close = text.IndexOf('>', 5);
        return close > 5 && text[close - 1] == '?' ? close + 1 : 0;
    }

    // text.upper().startswith("<!DOCTYPE"), with str.upper's full mapping (one code point may upper-case to several).
    private static bool UpperStartsWithDoctype(string text)
    {
        var upper = new StringBuilder();
        var offset = 0;
        while (upper.Length < DoctypeKeyword.Length && offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                upper.Append(text[offset]);
                offset++;
                continue;
            }

            upper.Append(EngineText.Upper(rune));
            offset += consumed;
        }

        return upper.ToString().StartsWith(DoctypeKeyword, StringComparison.Ordinal);
    }

    // The DOCTYPE scan: '[' opens, ']' closes when open, and the first '>' outside brackets ends it. 0 when none does.
    private static int DoctypeEnd(string working)
    {
        var depth = 0;
        for (var index = 0; index < working.Length; index++)
        {
            switch (working[index])
            {
                case '[':
                    depth++;
                    break;
                case ']' when depth > 0:
                    depth--;
                    break;
                case '>' when depth == 0 && index > 0:
                    return index + 1;
                default:
                    break;
            }
        }

        return 0;
    }
}
