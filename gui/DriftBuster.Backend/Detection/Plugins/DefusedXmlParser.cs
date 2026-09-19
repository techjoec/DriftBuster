using System.Xml;
using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// A clean-room XML well-formedness check and tree builder that accepts exactly the documents expat accepts in namespace mode
/// with entity declarations refused (the defusedxml guard): DOCTYPE allowed, nothing external read, a skipped entity reference
/// fails. <see cref="IsWellFormed"/> gives the verdict; <see cref="ParseTree"/> also builds elements and attributes.
/// </summary>
/// <remarks>
/// <para>
/// Prolog: tokenizer and declaration state machine matching expat, including where whitespace is required. A parameter-entity
/// reference in a non-standalone document stops processing of later internal-subset declarations; a DOCTYPE with an external id
/// or such a reference lets undeclared entity references in attribute values drop. Processed ENTITY declarations fail, except
/// redeclarations of the five predefined entities.
/// </para>
/// <para>
/// Content: XML 1.0 fourth-edition name tables (BMP only), bound prefixes, reserved <c>xml</c>/<c>xmlns</c>, no <c>}</c> in a
/// namespace name, no duplicate attributes by qualified or expanded name, internal-subset attribute defaults applied. Every
/// character must be an XML 1.0 character; an unpaired surrogate fails. Parsing is iterative, so depth is unbounded by stack.
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

    /// <summary>Thrown at the first point the parse would stop.</summary>
    private sealed class NotWellFormedException : Exception
    {
    }

    private static NotWellFormedException Fail() => new();

    /// <summary>True when the text is a well-formed document under the rules above.</summary>
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

    /// <summary>The root element, or null when the document is not well formed.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="insertComments">
    /// Keep comments inside the document element as <see cref="XComment"/> children (comments outside it are dropped).
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
        // One leading BOM is consumed.
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

    // The character at index, or a failure when the text ends there.
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
