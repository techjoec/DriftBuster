namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>The prolog: XML declaration, comments, processing instructions and the DOCTYPE with its internal subset.</summary>
internal sealed partial class DefusedXmlParser
{
    private enum State
    {
        Prolog0,
        Prolog1,
        Prolog2,
        Doctype0,
        Doctype1,
        Doctype2,
        Doctype3,
        Doctype4,
        Doctype5,
        InternalSubset,
        Entity0,
        Entity1,
        Entity2,
        Entity3,
        Entity4,
        Entity5,
        Entity6,
        Entity7,
        Entity8,
        Entity9,
        Entity10,
        Notation0,
        Notation1,
        Notation2,
        Notation3,
        Notation4,
        Attlist0,
        Attlist1,
        Attlist2,
        Attlist3,
        Attlist4,
        Attlist5,
        Attlist6,
        Attlist7,
        Attlist8,
        Attlist9,
        Element0,
        Element1,
        Element2,
        Element3,
        Element4,
        Element5,
        Element6,
        Element7,
        DeclarationClose,
    }

    private State _state = State.Prolog0;

    // DTD bookkeeping.
    private bool _standalone;
    private bool _hasParamEntityRefs;
    private bool _keepProcessing = true;

    // Reads tokens until the document element starts (_pos is left at its '<').
    private void ReadProlog()
    {
        while (true)
        {
            var token = NextPrologToken();
            if (token.Kind == Tok.End)
            {
                throw Fail();
            }

            if (token.Kind == Tok.InstanceStart)
            {
                if (_state is State.Prolog0 or State.Prolog1 or State.Prolog2)
                {
                    return;
                }

                throw Fail();
            }

            Dispatch(token);
        }
    }

    private void Dispatch(Token token)
    {
        switch (_state)
        {
            case State.Prolog0 or State.Prolog1 or State.Prolog2:
                PrologState(token);
                break;
            case >= State.Doctype0 and <= State.Doctype5:
                DoctypeState(token);
                break;
            case State.InternalSubset:
                InternalSubsetState(token);
                break;
            case >= State.Entity0 and <= State.Entity10:
                EntityState(token);
                break;
            case >= State.Notation0 and <= State.Notation4:
                NotationState(token);
                break;
            case >= State.Attlist0 and <= State.Attlist9:
                AttlistState(token);
                break;
            default:
                ElementState(token);
                break;
        }
    }

    // A token no rule of the current state accepts.
    private void Reject() => throw Fail();

    private bool Keyword(Token token, string keyword) => token.Kind == Tok.Name && SpanEquals(token.Start, token.End, keyword);

    private void PrologState(Token token)
    {
        switch (token.Kind)
        {
            case Tok.Space or Tok.ProcessingInstruction or Tok.Comment:
                _state = _state == State.Prolog0 ? State.Prolog1 : _state;
                break;
            case Tok.XmlDeclaration when _state == State.Prolog0:
                ParseXmlDeclaration(token.Start, token.End);
                _state = State.Prolog1;
                break;
            case Tok.DeclarationOpen when _state != State.Prolog2 && SpanEquals(token.Start, token.End, "DOCTYPE"):
                _state = State.Doctype0;
                break;
            default:
                Reject();
                break;
        }
    }

    private void DoctypeState(Token token)
    {
        if (token.Kind == Tok.Space)
        {
            return;
        }

        _state = (_state, token.Kind) switch
        {
            (State.Doctype0, Tok.Name or Tok.PrefixedName) => State.Doctype1,
            (State.Doctype1, _) when Keyword(token, "SYSTEM") => State.Doctype3,
            (State.Doctype1, _) when Keyword(token, "PUBLIC") => State.Doctype2,
            (State.Doctype1 or State.Doctype4, Tok.OpenBracket) => State.InternalSubset,
            (State.Doctype1 or State.Doctype4 or State.Doctype5, Tok.DeclarationClose) => State.Prolog2,
            (State.Doctype2, Tok.Literal) => PublicId(token, State.Doctype3),
            (State.Doctype3, Tok.Literal) => SystemId(State.Doctype4),
            _ => throw Fail(),
        };
    }

    // An external identifier names a subset that is never read: undeclared entity references in attribute values stop being
    // errors from here on.
    private State PublicId(Token literal, State next)
    {
        CheckPublicId(literal);
        _hasParamEntityRefs = true;
        return next;
    }

    private State SystemId(State next)
    {
        _hasParamEntityRefs = true;
        return next;
    }

    // PubidChar: space, CR, LF, ASCII letters and digits, and -'()+,./:=?;!*#@$_%.
    private void CheckPublicId(Token literal)
    {
        for (var index = literal.Start + 1; index < literal.End - 1; index++)
        {
            var ch = _text[index];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '\r' or '\n' || "-'()+,./:=?;!*#@$_%".Contains(ch, StringComparison.Ordinal)))
            {
                throw Fail();
            }
        }
    }

    private void InternalSubsetState(Token token)
    {
        switch (token.Kind)
        {
            case Tok.Space or Tok.ProcessingInstruction or Tok.Comment:
                return;
            case Tok.CloseBracket:
                _state = State.Doctype5;
                return;
            case Tok.ParamEntityRef:
                // The reference is never resolved; unless the document is standalone, every later declaration is
                // read for syntax only.
                _hasParamEntityRefs = true;
                _keepProcessing = _standalone;
                return;
            case Tok.DeclarationOpen:
                _state = DeclarationKeyword(token);
                return;
            default:
                Reject();
                return;
        }
    }

    private State DeclarationKeyword(Token token)
    {
        if (SpanEquals(token.Start, token.End, "ENTITY"))
        {
            return State.Entity0;
        }

        if (SpanEquals(token.Start, token.End, "ATTLIST"))
        {
            return State.Attlist0;
        }

        if (SpanEquals(token.Start, token.End, "ELEMENT"))
        {
            return State.Element0;
        }

        return SpanEquals(token.Start, token.End, "NOTATION") ? State.Notation0 : throw Fail();
    }

    private void NotationState(Token token)
    {
        if (token.Kind == Tok.Space)
        {
            return;
        }

        _state = (_state, token.Kind) switch
        {
            (State.Notation0, Tok.Name) => State.Notation1,
            (State.Notation1, _) when Keyword(token, "SYSTEM") => State.Notation3,
            (State.Notation1, _) when Keyword(token, "PUBLIC") => State.Notation2,
            (State.Notation2, Tok.Literal) => NotationPublicId(token),
            (State.Notation3 or State.Notation4, Tok.Literal) => State.DeclarationClose,
            (State.Notation4, Tok.DeclarationClose) => State.InternalSubset,
            _ => throw Fail(),
        };
    }

    private State NotationPublicId(Token literal)
    {
        CheckPublicId(literal);
        return State.Notation4;
    }

    /// <summary>
    /// The pseudo-attributes between "&lt;?xml" and "?&gt;": <c>version</c> first and required, then optional
    /// <c>encoding</c> (its value starting with a letter) and <c>standalone</c> (<c>yes</c> or <c>no</c>), each
    /// preceded by white space, with values of ASCII letters, digits, '.', '_' and '-' only. The encoding is otherwise
    /// ignored: the text was handed over as UTF-8.
    /// </summary>
    private void ParseXmlDeclaration(int start, int end)
    {
        var index = start;
        if (!NextPseudoAttribute(ref index, end, out var name, out var valueStart, out var valueEnd) || !string.Equals(name, "version", StringComparison.Ordinal))
        {
            throw Fail();
        }

        if (!NextPseudoAttribute(ref index, end, out name, out valueStart, out valueEnd))
        {
            return;
        }

        if (string.Equals(name, "encoding", StringComparison.Ordinal))
        {
            if (valueStart == valueEnd || !char.IsAsciiLetter(_text[valueStart]))
            {
                throw Fail();
            }

            if (!NextPseudoAttribute(ref index, end, out name, out valueStart, out valueEnd))
            {
                return;
            }
        }

        if (!string.Equals(name, "standalone", StringComparison.Ordinal))
        {
            throw Fail();
        }

        _standalone = SpanEquals(valueStart, valueEnd, "yes") || (SpanEquals(valueStart, valueEnd, "no") ? false : throw Fail());
        while (index < end && IsSpace(_text[index]))
        {
            index++;
        }

        if (index != end)
        {
            throw Fail();
        }
    }

    // False when only white space remains; a malformed pseudo-attribute fails.
    private bool NextPseudoAttribute(ref int index, int end, out string? name, out int valueStart, out int valueEnd)
    {
        (name, valueStart, valueEnd) = (null, 0, 0);
        if (index == end)
        {
            return false;
        }

        if (!IsSpace(_text[index]))
        {
            throw Fail();
        }

        while (index < end && IsSpace(_text[index]))
        {
            index++;
        }

        if (index == end)
        {
            return false;
        }

        var nameStart = index;
        while (index < end && _text[index] != '=' && !IsSpace(_text[index]) && _text[index] < '\x80')
        {
            index++;
        }

        if (index == end || _text[index] >= '\x80' || index == nameStart)
        {
            throw Fail();
        }

        name = _text[nameStart..index];
        index = SkipSpaces(index, end);
        if (index == end || _text[index] != '=')
        {
            throw Fail();
        }

        index = SkipSpaces(index + 1, end);
        (valueStart, valueEnd) = PseudoValue(ref index, end);
        return true;
    }

    private int SkipSpaces(int index, int end)
    {
        while (index < end && IsSpace(_text[index]))
        {
            index++;
        }

        return index;
    }

    private (int Start, int End) PseudoValue(ref int index, int end)
    {
        if (index == end || _text[index] is not ('"' or '\''))
        {
            throw Fail();
        }

        var quote = _text[index];
        var start = ++index;
        while (index < end && _text[index] != quote)
        {
            if (!(char.IsAsciiLetterOrDigit(_text[index]) || _text[index] is '.' or '_' or '-'))
            {
                throw Fail();
            }

            index++;
        }

        if (index == end)
        {
            throw Fail();
        }

        index++;
        return (start, index - 1);
    }
}
