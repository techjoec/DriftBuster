using System.Xml;
using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// <c>defusedxml.ElementTree.fromstring(text)</c> as the Python XML plugin runs it: expat in namespace mode (the
/// ElementTree parser's <c>"}"</c> separator) fed the text as UTF-8, with defusedxml's defaults (a DOCTYPE is allowed,
/// every entity declaration expat processes raises, nothing external is read) and ElementTree's default handler, which
/// raises on an entity reference expat reports as skipped. <see cref="IsWellFormed"/> is the verdict;
/// <see cref="ParseTree"/> also builds the element tree (elements and attributes only) the plugin walks.
/// </summary>
/// <remarks>
/// <para>
/// The prolog is read by a tokenizer and a declaration state machine that accept exactly what expat's do, including
/// which delimiters may follow a name or a literal without white space. Internal-subset declarations are checked in
/// order with expat's bookkeeping: a parameter-entity reference in a document that is not <c>standalone="yes"</c>
/// stops the processing of every later declaration (their values are neither checked nor applied), and a DOCTYPE
/// with an external identifier or such a reference relaxes undeclared entity references in attribute values (they are
/// dropped). An entity declaration that is processed is refused as defusedxml refuses it, unless it redeclares one of
/// the five predefined entities, which expat ignores after checking its value.
/// </para>
/// <para>
/// Content follows expat's tokenizer and namespace processing: names use the XML 1.0 fourth-edition name tables
/// (<see cref="XmlConvert.IsStartNCNameChar"/> and <see cref="XmlConvert.IsNCNameChar"/> equal expat's over the whole
/// BMP, and no supplementary character is a name character), prefixes must be bound, <c>xml</c>/<c>xmlns</c> and
/// their namespace names are reserved, a namespace name may not contain <c>}</c>, an attribute may not repeat by
/// qualified or expanded name, and attribute defaults declared in the internal subset (namespace declarations
/// included) apply. Every character must be an XML 1.0 character; an unpaired surrogate fails the UTF-8 encoding
/// Python performs first. Parsing is iterative, so nesting depth is bounded only by the text.
/// </para>
/// </remarks>
internal sealed partial class DefusedXmlParser
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private readonly string _text;
    private readonly bool _buildTree;
    private readonly bool _insertComments;
    private readonly bool _canonical;
    private int _pos;

    private DefusedXmlParser(string text, bool buildTree, bool insertComments = false, bool canonical = false)
    {
        _text = text;
        _buildTree = buildTree || canonical;
        _insertComments = insertComments || canonical;
        _canonical = canonical;
    }

    /// <summary>Thrown at the first point expat (or defusedxml's handlers) would stop the parse.</summary>
    private sealed class NotWellFormedException : Exception
    {
    }

    private static NotWellFormedException Fail() => new();

    /// <summary>True when <c>defusedxml.ElementTree.fromstring(text)</c> returns without raising.</summary>
    public static bool IsWellFormed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            new DefusedXmlParser(text, buildTree: false).Parse();
            return true;
        }
        catch (NotWellFormedException)
        {
            return false;
        }
    }

    /// <summary>The root element <c>defusedxml.ElementTree.fromstring(text)</c> returns, or null where it raises.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="insertComments">
    /// <c>TreeBuilder(insert_comments=True)</c>: every comment inside the document element becomes an <see cref="XComment"/>
    /// child of the element open around it (comments before or after the document element are dropped, as TreeBuilder
    /// drops them with no element open). Only the plugin's no-defusedxml fallback branch asks for them; that branch parses
    /// with plain expat, which accepts the same documents here because the plugin refuses every ENTITY declaration first.
    /// </param>
    public static XElement? ParseTree(string text, bool insertComments = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return new DefusedXmlParser(text, buildTree: true, insertComments).Parse();
        }
        catch (NotWellFormedException)
        {
            return null;
        }
    }

    private XElement? Parse()
    {
        // expat consumes one leading byte-order mark.
        if (_text.StartsWith('﻿'))
        {
            _pos = 1;
        }

        ReadProlog();
        var root = ReadContent();
        ReadEpilog();
        return root;
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Peek(int offset = 0) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    // The character at index, or a failure when the text ends there (expat's partial token at the final buffer).
    private char Require(int index) => index < _text.Length ? _text[index] : throw Fail();

    private static bool IsSpace(char ch) => ch is ' ' or '\t' or '\n' or '\r';

    private static bool IsAsciiNameStart(char ch) => char.IsAsciiLetter(ch) || ch == '_';

    // A BMP code point that starts a namespace-mode name; supplementary characters never do.
    private static bool IsNameStart(char ch) => !char.IsSurrogate(ch) && XmlConvert.IsStartNCNameChar(ch);

    private static bool IsNameChar(char ch) => !char.IsSurrogate(ch) && XmlConvert.IsNCNameChar(ch);

    // The width of the XML 1.0 character at index (2 for a surrogate pair), or a failure.
    private int CharWidth(int index)
    {
        var ch = _text[index];
        if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch <= '퟿') || (ch >= '' && ch <= '�'))
        {
            return 1;
        }

        if (char.IsHighSurrogate(ch) && index + 1 < _text.Length && char.IsLowSurrogate(_text[index + 1]))
        {
            return 2;
        }

        throw Fail();
    }

    private bool SpanEquals(int start, int end, string value)
        => end - start == value.Length && _text.AsSpan(start, end - start).SequenceEqual(value);

    // After "<!--": characters up to "-->", where "--" must end the comment. _pos lands after the comment.
    private void ScanComment(int index)
    {
        while (true)
        {
            if (Require(index) != '-')
            {
                index += CharWidth(index);
                continue;
            }

            index++;
            if (Require(index) != '-')
            {
                continue;
            }

            if (Require(index + 1) != '>')
            {
                throw Fail();
            }

            _pos = index + 2;
            return;
        }
    }

    /// <summary>
    /// After "&lt;?": a name target (no colon), then "?&gt;" directly or after white space and any characters. Returns
    /// true for the target <c>xml</c> exactly (an XML or text declaration); any other casing of it is invalid. _pos
    /// lands after "?&gt;"; <paramref name="bodyStart"/> is where the characters after the target begin.
    /// </summary>
    private bool ScanProcessingInstruction(int index, out int bodyStart)
    {
        var targetStart = index;
        if (!IsNameStart(Require(index)))
        {
            throw Fail();
        }

        do
        {
            index++;
        }
        while (IsNameChar(Require(index)));

        bodyStart = index;
        var isXml = CheckPiTarget(targetStart, index);
        var next = _text[index];
        if (next == '?')
        {
            _pos = Require(index + 1) == '>' ? index + 2 : throw Fail();
            return isXml;
        }

        if (!IsSpace(next))
        {
            throw Fail();
        }

        index++;
        while (!(Require(index) == '?' && Require(index + 1) == '>'))
        {
            index += CharWidth(index);
        }

        _pos = index + 2;
        return isXml;
    }

    private bool CheckPiTarget(int start, int end)
    {
        if (end - start != 3 || (_text[start] | 0x20) != 'x' || (_text[start + 1] | 0x20) != 'm' || (_text[start + 2] | 0x20) != 'l')
        {
            return false;
        }

        return SpanEquals(start, end, "xml") ? true : throw Fail();
    }
}
