using System.Text;
using System.Xml.Linq;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>The document element and everything after it: tags, character data, references, CDATA sections and the epilog.</summary>
internal sealed partial class DefusedXmlParser
{
    /// <summary>An open element: its qualified name as written, the bindings it declared, and its tree node.</summary>
    private sealed record OpenElement(string QualifiedName, int BindingMark, XElement? Node);

    // Reads from the document element's '<' to the end of its end tag; returns the tree root when building one.
    private XElement? ReadContent()
    {
        var open = new Stack<OpenElement>();
        XElement? root = null;
        do
        {
            if (AtEnd)
            {
                throw Fail();
            }

            switch (_text[_pos])
            {
                case '<':
                    FlushCanonicalText(open);
                    ReadMarkup(open, ref root);
                    break;
                case '&':
                    var referenceStart = _pos;
                    _pos = ScanReference(_pos, _text.Length, out var isCharacter, out var nameSpan);
                    RequireContentReference(isCharacter, nameSpan);
                    AppendCanonicalReference(open, referenceStart, nameSpan);
                    break;
                default:
                    var dataStart = _pos;
                    ReadCharacterData();
                    AppendCanonicalText(open, dataStart, _pos);
                    break;
            }
        }
        while (open.Count > 0);

        return root;
    }

    private void ReadMarkup(Stack<OpenElement> open, ref XElement? root)
    {
        var next = Require(_pos + 1);
        switch (next)
        {
            case '/':
                ReadEndTag(open);
                return;
            case '?':
                // An XML declaration is misplaced in content.
                if (ScanProcessingInstruction(_pos + 2, out _))
                {
                    throw Fail();
                }

                return;
            case '!':
                ReadCommentOrCdata(open.Count > 0 ? open.Peek().Node : null);
                return;
            default:
                if (!IsNameStart(next))
                {
                    throw Fail();
                }

                var element = ReadStartTag(open.Count > 0 ? open.Peek().Node : null, out var empty);
                root ??= element.Node;
                if (empty)
                {
                    PopBindings(element.BindingMark);
                }
                else
                {
                    open.Push(element);
                }

                return;
        }
    }

    // Only the predefined entities can be referenced; any other name is undeclared (every processed declaration was refused) and fails.
    private void RequireContentReference(bool isCharacter, Range nameSpan)
    {
        if (!isCharacter && !PredefinedEntities.Contains(_text[nameSpan]))
        {
            throw Fail();
        }
    }

    // Character data up to the next '<' or '&': XML characters only, and never "]]>".
    private void ReadCharacterData()
    {
        while (!AtEnd && _text[_pos] is not ('<' or '&'))
        {
            if (_text[_pos] == ']' && Peek(1) == ']' && Peek(2) == '>')
            {
                throw Fail();
            }

            _pos += CharWidth(_pos);
        }
    }

    // parent is the element open around the markup (null at top level, and always null unless a tree is being built).
    private void ReadCommentOrCdata(XElement? parent)
    {
        var next = Require(_pos + 2);
        if (next == '-')
        {
            if (Require(_pos + 3) != '-')
            {
                throw Fail();
            }

            var start = _pos + 4;
            ScanComment(start);
            if (_insertComments && parent is not null)
            {
                // Comment data with line ends normalised to LF.
                parent.Add(new XComment(NormaliseLineEnds(_text[start..(_pos - 3)])));
            }

            return;
        }

        if (next != '[' || !_text.AsSpan(_pos + 3).StartsWith("CDATA[", StringComparison.Ordinal))
        {
            throw Fail();
        }

        var index = _pos + 9;
        while (!(Require(index) == ']' && Require(index + 1) == ']' && Require(index + 2) == '>'))
        {
            index += CharWidth(index);
        }

        if (_canonical && parent is not null)
        {
            _pendingText.Append(NormaliseLineEnds(_text[(_pos + 9)..index]));
        }

        _pos = index + 3;
    }

    // "</" name, optional white space, '>'; the name must be the open element's qualified name exactly.
    private void ReadEndTag(Stack<OpenElement> open)
    {
        var nameStart = _pos + 2;
        if (!IsNameStart(Require(nameStart)))
        {
            throw Fail();
        }

        var index = nameStart + 1;
        while (IsNameChar(Require(index)) || _text[index] == ':')
        {
            index++;
        }

        var nameEnd = index;
        while (IsSpace(Require(index)))
        {
            index++;
        }

        if (_text[index] != '>' || open.Count == 0 || !SpanEquals(nameStart, nameEnd, open.Peek().QualifiedName))
        {
            throw Fail();
        }

        PopBindings(open.Pop().BindingMark);
        _pos = index + 1;
    }

    /// <summary>
    /// A reference starting at the '&amp;' at <paramref name="index"/> and ending before <paramref name="end"/>:
    /// "&amp;#" decimal digits ";" or "&amp;#x" hexadecimal digits ";" naming an XML character (below U+110000, not a
    /// surrogate, U+FFFE, U+FFFF or a C0 control other than tab, LF and CR), or "&amp;" name ";" with no colon. Returns the
    /// index after the ';'.
    /// </summary>
    private int ScanReference(int index, int end, out bool isCharacter, out Range nameSpan)
    {
        nameSpan = default;
        var first = ReferenceChar(index + 1, end);
        isCharacter = first == '#';
        if (!isCharacter)
        {
            var nameEnd = index + 2;
            if (!IsNameStart(first))
            {
                throw Fail();
            }

            while (IsNameChar(ReferenceChar(nameEnd, end)))
            {
                nameEnd++;
            }

            nameSpan = (index + 1)..nameEnd;
            return ReferenceChar(nameEnd, end) == ';' ? nameEnd + 1 : throw Fail();
        }

        return ScanCharacterReference(index + 2, end, out _);
    }

    private char ReferenceChar(int index, int end) => index < end ? _text[index] : throw Fail();

    private int ScanCharacterReference(int index, int end, out int codePoint)
    {
        var hex = ReferenceChar(index, end) == 'x';
        if (hex)
        {
            index++;
        }

        codePoint = 0;
        var digits = 0;
        while (true)
        {
            var ch = ReferenceChar(index, end);
            var digit = char.IsAsciiDigit(ch) ? ch - '0' : hex && char.IsAsciiHexDigit(ch) ? (ch | 0x20) - 'a' + 10 : -1;
            if (digit < 0)
            {
                break;
            }

            codePoint = (codePoint * (hex ? 16 : 10)) + digit;
            if (codePoint >= 0x110000)
            {
                throw Fail();
            }

            digits++;
            index++;
        }

        if (digits == 0 || _text[index] != ';' || !IsCharacterReferenceTarget(codePoint))
        {
            throw Fail();
        }

        return index + 1;
    }

    private static bool IsCharacterReferenceTarget(int codePoint)
        => codePoint is 0x9 or 0xA or 0xD || (codePoint >= 0x20 && codePoint < 0xD800) || (codePoint > 0xDFFF && codePoint is not (0xFFFE or 0xFFFF));

    /// <summary>
    /// Attribute-value normalisation over [start, end): tab, LF, CR and CRLF become one space; character and predefined entity
    /// references expand; '&lt;' and malformed references fail; an undeclared entity fails when declarations are checked
    /// (standalone, or no external subset or parameter-entity reference yet) and is dropped otherwise. Non-CDATA values also trim
    /// and collapse spaces.
    /// </summary>
    private string NormalizeAttributeValue(int start, int end, bool isCdata)
    {
        var value = new StringBuilder(end - start);
        var index = start;
        while (index < end)
        {
            var ch = _text[index];
            if (ch == '&')
            {
                index = AppendReference(value, index, end, isCdata);
                continue;
            }

            if (ch == '<')
            {
                throw Fail();
            }

            if (ch is ' ' or '\t' or '\n' or '\r')
            {
                index += ch == '\r' && index + 1 < end && _text[index + 1] == '\n' ? 2 : 1;
                AppendSpace(value, isCdata);
                continue;
            }

            var width = CharWidth(index);
            value.Append(_text, index, width);
            index += width;
        }

        if (!isCdata && value.Length > 0 && value[^1] == ' ')
        {
            value.Length--;
        }

        return value.ToString();
    }

    private static void AppendSpace(StringBuilder value, bool isCdata)
    {
        if (isCdata || (value.Length > 0 && value[^1] != ' '))
        {
            value.Append(' ');
        }
    }

    private int AppendReference(StringBuilder value, int index, int end, bool isCdata)
    {
        if (ReferenceChar(index + 1, end) == '#')
        {
            var after = ScanCharacterReference(index + 2, end, out var codePoint);
            if (codePoint == ' ')
            {
                AppendSpace(value, isCdata);
            }
            else
            {
                value.Append(char.ConvertFromUtf32(codePoint));
            }

            return after;
        }

        var next = ScanReference(index, end, out _, out var nameSpan);
        var name = _text[nameSpan];
        if (PredefinedEntities.Contains(name))
        {
            value.Append(name switch
            {
                "lt" => '<',
                "gt" => '>',
                "amp" => '&',
                "quot" => '"',
                _ => '\'',
            });
        }
        else if (_standalone || !_hasParamEntityRefs)
        {
            throw Fail();
        }

        return next;
    }

    // After the document element: white space, comments and processing instructions only.
    private void ReadEpilog()
    {
        while (true)
        {
            var token = NextPrologToken();
            switch (token.Kind)
            {
                case Tok.End:
                    return;
                case Tok.Space or Tok.Comment or Tok.ProcessingInstruction:
                    break;
                default:
                    throw Fail();
            }
        }
    }
}
