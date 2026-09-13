using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>The prolog tokenizer: the tokens expat's prolog scanner yields, with its rules for what may follow each.</summary>
internal sealed partial class DefusedXmlParser
{
    private enum Tok
    {
        End,
        Space,
        ProcessingInstruction,
        XmlDeclaration,
        Comment,
        DeclarationOpen,
        InstanceStart,
        Literal,
        ParamEntityRef,
        Percent,
        Name,
        PrefixedName,
        NameToken,
        NameQuestion,
        NameAsterisk,
        NamePlus,
        PoundName,
        OpenParen,
        CloseParen,
        CloseParenQuestion,
        CloseParenAsterisk,
        CloseParenPlus,
        Or,
        Comma,
        OpenBracket,
        CloseBracket,
        DeclarationClose,
    }

    /// <summary>
    /// A prolog token spanning [Start, End): a name's own characters, a literal with its quotes, a declaration's keyword,
    /// a parameter-entity reference with its '%' and ';', a declaration's attribute region (after "&lt;?xml").
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Token(Tok Kind, int Start, int End);

    // Every token expat reports as invalid or unfinished fails here: no prolog, DOCTYPE or epilog state accepts one.
    private Token NextPrologToken()
    {
        if (AtEnd)
        {
            return new Token(Tok.End, _pos, _pos);
        }

        var start = _pos;
        var ch = _text[_pos];
        switch (ch)
        {
            case ' ' or '\t' or '\n' or '\r':
                while (!AtEnd && IsSpace(_text[_pos]))
                {
                    _pos++;
                }

                return new Token(Tok.Space, start, _pos);
            case '<':
                return ScanMarkupOpen(start);
            case '"' or '\'':
                return ScanLiteral(start);
            case '%':
                return ScanPercent(start);
            case '#':
                return ScanPoundName(start);
            case ']':
                // "]]>" closes a conditional section, which the document entity never opens.
                if (Require(_pos + 1) == ']' && Require(_pos + 2) == '>')
                {
                    throw Fail();
                }

                return Single(Tok.CloseBracket);
            case ')':
                return ScanCloseParen(start);
            default:
                return ch switch
                {
                    '[' => Single(Tok.OpenBracket),
                    '(' => Single(Tok.OpenParen),
                    '|' => Single(Tok.Or),
                    ',' => Single(Tok.Comma),
                    '>' => Single(Tok.DeclarationClose),
                    _ => ScanNameToken(start),
                };
        }
    }

    private Token Single(Tok kind)
    {
        _pos++;
        return new Token(kind, _pos - 1, _pos);
    }

    private Token ScanMarkupOpen(int start)
    {
        var next = Require(start + 1);
        if (next == '!')
        {
            return ScanDeclarationOpen(start + 2);
        }

        if (next == '?')
        {
            var isXml = ScanProcessingInstruction(start + 2, out var bodyStart);
            return isXml ? new Token(Tok.XmlDeclaration, bodyStart, _pos - 2) : new Token(Tok.ProcessingInstruction, start, _pos);
        }

        // Any ASCII name start or non-ASCII character starts the document element; content scanning judges it.
        if (IsAsciiNameStart(next) || next >= '\x80')
        {
            return new Token(Tok.InstanceStart, start, start);
        }

        throw Fail();
    }

    // After "<!": a comment, or a declaration keyword of ASCII letters and '_' ended by white space or a '%' that does
    // not itself end the token. "<![" opens a conditional section, which the document entity rejects.
    private Token ScanDeclarationOpen(int index)
    {
        var first = Require(index);
        if (first == '-')
        {
            if (Require(index + 1) != '-')
            {
                throw Fail();
            }

            ScanComment(index + 2);
            return new Token(Tok.Comment, index - 2, _pos);
        }

        if (!IsAsciiNameStart(first))
        {
            throw Fail();
        }

        var keywordStart = index;
        while (true)
        {
            var ch = Require(++index);
            if (IsAsciiNameStart(ch))
            {
                continue;
            }

            if (ch == '%' && Require(index + 1) is ' ' or '\t' or '\n' or '\r' or '%')
            {
                throw Fail();
            }

            if (ch is '%' || IsSpace(ch))
            {
                _pos = index;
                return new Token(Tok.DeclarationOpen, keywordStart, index);
            }

            throw Fail();
        }
    }

    // A literal must be followed by white space, '>', '%' or '['.
    private Token ScanLiteral(int start)
    {
        var quote = _text[start];
        var index = start + 1;
        while (Require(index) != quote)
        {
            index += CharWidth(index);
        }

        index++;
        _pos = Require(index) is ' ' or '\t' or '\n' or '\r' or '>' or '%' or '[' ? index : throw Fail();
        return new Token(Tok.Literal, start, index);
    }

    // "%name;" is a parameter-entity reference; '%' before white space or another '%' stands alone.
    private Token ScanPercent(int start)
    {
        var next = Require(start + 1);
        if (IsSpace(next) || next == '%')
        {
            _pos = start + 1;
            return new Token(Tok.Percent, start, start + 1);
        }

        var index = ScanNameChars(start + 1);
        if (Require(index) != ';')
        {
            throw Fail();
        }

        _pos = index + 1;
        return new Token(Tok.ParamEntityRef, start, _pos);
    }

    // A name start then name characters, with no colon; returns the index after them.
    private int ScanNameChars(int index)
    {
        if (!IsNameStart(Require(index)))
        {
            throw Fail();
        }

        do
        {
            index++;
        }
        while (IsNameChar(Require(index)));

        return index;
    }

    private Token ScanPoundName(int start)
    {
        var end = ScanNameChars(start + 1);
        _pos = _text[end] is ' ' or '\t' or '\n' or '\r' or ')' or '>' or '%' or '|' ? end : throw Fail();
        return new Token(Tok.PoundName, start + 1, end);
    }

    private Token ScanCloseParen(int start)
    {
        var next = Require(start + 1);
        var (kind, length) = next switch
        {
            '*' => (Tok.CloseParenAsterisk, 2),
            '?' => (Tok.CloseParenQuestion, 2),
            '+' => (Tok.CloseParenPlus, 2),
            ' ' or '\t' or '\n' or '\r' or '>' or ',' or '|' or ')' => (Tok.CloseParen, 1),
            _ => throw Fail(),
        };
        _pos = start + length;
        return new Token(kind, start, _pos);
    }

    /// <summary>
    /// A name (ASCII letter, '_' or non-ASCII name start first), a name token (digit, '.', '-', ':' or another name
    /// character first), or a prefixed name (a name, one colon, a name character); a second colon demotes it to a
    /// name token. A name may carry a '?', '*' or '+' suffix; only white space, '&gt;', ')', ',', '|', '[' or '%' may
    /// otherwise follow.
    /// </summary>
    private Token ScanNameToken(int start)
    {
        var ch = _text[start];
        var kind = ch is ':' || char.IsAsciiDigit(ch) || ch is '.' or '-' ? Tok.NameToken
            : IsNameStart(ch) ? Tok.Name
            : IsNameChar(ch) && ch >= '\x80' ? Tok.NameToken
            : throw Fail();
        var index = start + 1;
        while (true)
        {
            var next = Require(index);
            if (IsNameChar(next))
            {
                index++;
            }
            else if (next == ':')
            {
                kind = ColonIn(kind, ++index);
            }
            else if (next is '?' or '*' or '+')
            {
                return NameSuffix(kind, start, index, next);
            }
            else if (next is ' ' or '\t' or '\n' or '\r' or '>' or ')' or ',' or '|' or '[' or '%')
            {
                _pos = index;
                return new Token(kind, start, index);
            }
            else
            {
                throw Fail();
            }
        }
    }

    // A colon makes a name prefixed when a name character follows it (a non-ASCII non-name character is invalid, any
    // other character leaves a name token); a colon in a prefixed name makes a name token.
    private Tok ColonIn(Tok kind, int index)
    {
        if (kind == Tok.Name)
        {
            var next = Require(index);
            return IsNameChar(next) ? Tok.PrefixedName : next >= '\x80' ? throw Fail() : Tok.NameToken;
        }

        return kind == Tok.PrefixedName ? Tok.NameToken : kind;
    }

    private Token NameSuffix(Tok kind, int start, int index, char suffix)
    {
        if (kind == Tok.NameToken)
        {
            throw Fail();
        }

        _pos = index + 1;
        var suffixed = suffix switch
        {
            '?' => Tok.NameQuestion,
            '*' => Tok.NameAsterisk,
            _ => Tok.NamePlus,
        };
        return new Token(suffixed, start, index);
    }
}
