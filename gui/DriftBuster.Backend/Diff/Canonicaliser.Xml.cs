using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

public static partial class Canonicaliser
{
    private const string DoctypeKeyword = "<!DOCTYPE";

    /// <summary>
    /// XML declaration and DOCTYPE kept verbatim; the rest parsed (comments kept) and re-serialised with sorted attributes,
    /// whitespace-only attribute values, text and comments collapsed to empty, and the prolog joined in front with LF. A payload that
    /// does not parse goes through <see cref="CanonicaliseText"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Namespace prefixes stay as written and each <c>xmlns</c> declaration stays on its element (sorted by prefix, before the attributes),
    /// so QNames inside attribute values keep pointing at a real prefix. Attributes order by <c>{uri}local</c>.
    /// </para>
    /// <para>
    /// Parsed with <see cref="SafeXml"/>: a document using an entity its DOCTYPE declares canonicalises as text and no entity is
    /// expanded. Serialisation uses an explicit stack.
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
        var working = payload.TrimStart();

        var declarationEnd = MatchXmlDeclaration(working);
        if (declarationEnd > 0)
        {
            xmlDeclaration = working[..declarationEnd];
            working = working[declarationEnd..].TrimStart();
        }

        if (working.StartsWith(DoctypeKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var end = DoctypeEnd(working);
            if (end > 0)
            {
                doctype = working[..end];
                working = working[end..].TrimStart();
            }
            else
            {
                // A DOCTYPE the scan cannot close: parse the original payload with no captured prolog.
                working = payload;
                xmlDeclaration = string.Empty;
            }
        }

        var root = SafeXml.TryLoadRoot(working, keepComments: true);
        if (root is null)
        {
            return CanonicaliseText(payload);
        }

        var serialised = SerialiseXml(root);
        var prolog = string.Join("\n", new[] { xmlDeclaration, doctype }.Where(part => part.Length > 0));
        return prolog.Length > 0 ? prolog + "\n" + serialised : serialised;
    }

    // "<?xml" in any ASCII case, then the first '>' must directly follow a '?' outside "<?xml"; the match end, or 0.
    private static int MatchXmlDeclaration(string text)
    {
        if (text.Length < 7 || text[0] != '<' || text[1] != '?' || (text[2] | 0x20) != 'x' || (text[3] | 0x20) != 'm' || (text[4] | 0x20) != 'l')
        {
            return 0;
        }

        var close = text.IndexOf('>', 5);
        return close > 5 && text[close - 1] == '?' ? close + 1 : 0;
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
