namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>ENTITY, ATTLIST and ELEMENT declarations in the internal subset, with the checks expat makes on their values.</summary>
internal sealed partial class DefusedXmlParser
{
    private static readonly HashSet<string> PredefinedEntities = new(StringComparer.Ordinal) { "lt", "gt", "amp", "quot", "apos" };

    private static readonly HashSet<string> AttributeTypes = new(StringComparer.Ordinal)
    {
        "CDATA", "ID", "IDREF", "IDREFS", "ENTITY", "ENTITIES", "NMTOKEN", "NMTOKENS",
    };

    /// <summary>One declared attribute of an element type, in declaration order; Value is null for #IMPLIED and #REQUIRED.</summary>
    private sealed record AttributeDefault(string Name, bool IsCdata, string? Value);

    // Element type name -> its attribute declarations, as expat's defineAttribute keeps them.
    private readonly Dictionary<string, List<AttributeDefault>> _attributeDefaults = new(StringComparer.Ordinal);

    // The ENTITY declaration being read is one expat reports to defusedxml's handler, which refuses it.
    private bool _entityReported;
    private string _declElement = string.Empty;
    private string _declAttribute = string.Empty;
    private bool _declAttributeIsCdata;

    // Content-model connector per group level: '\0' until the group's first ',' or '|'.
    private readonly List<char> _groupConnectors = [];
    private int _groupLevel;

    private string TokenText(Token token) => _text[token.Start..token.End];

    private void EntityState(Token token)
    {
        if (token.Kind == Tok.Space)
        {
            return;
        }

        _state = (_state, token.Kind) switch
        {
            (State.Entity0, Tok.Percent) => State.Entity1,
            (State.Entity0, Tok.Name) => GeneralEntityName(token),
            (State.Entity1, Tok.Name) => ParamEntityName(),
            (State.Entity2, _) when Keyword(token, "SYSTEM") => State.Entity4,
            (State.Entity2, _) when Keyword(token, "PUBLIC") => State.Entity3,
            (State.Entity2 or State.Entity7, Tok.Literal) => EntityValue(token),
            (State.Entity3, Tok.Literal) => EntityPublicId(token, State.Entity4),
            (State.Entity4, Tok.Literal) => State.Entity5,
            (State.Entity5 or State.Entity10, Tok.DeclarationClose) => EntityComplete(),
            (State.Entity5, _) when Keyword(token, "NDATA") => State.Entity6,
            (State.Entity6, Tok.Name) => EntityReported(State.DeclarationClose),
            (State.Entity7, _) when Keyword(token, "SYSTEM") => State.Entity9,
            (State.Entity7, _) when Keyword(token, "PUBLIC") => State.Entity8,
            (State.Entity8, Tok.Literal) => EntityPublicId(token, State.Entity9),
            (State.Entity9, Tok.Literal) => State.Entity10,
            _ => throw Fail(),
        };
    }

    // Redeclaring a predefined entity is ignored; any other declaration expat processes reaches the handler.
    private State GeneralEntityName(Token name)
    {
        _entityReported = _keepProcessing && !PredefinedEntities.Contains(TokenText(name));
        return State.Entity2;
    }

    private State ParamEntityName()
    {
        _entityReported = _keepProcessing;
        return State.Entity7;
    }

    private State EntityPublicId(Token literal, State next)
    {
        CheckPublicId(literal);
        return next;
    }

    // The value of a processed declaration is checked before the handler sees it.
    private State EntityValue(Token literal)
    {
        if (_keepProcessing)
        {
            CheckEntityValue(literal.Start + 1, literal.End - 1);
        }

        return EntityReported(State.DeclarationClose);
    }

    private State EntityComplete() => EntityReported(State.InternalSubset);

    private State EntityReported(State next) => _entityReported ? throw Fail() : next;

    // An entity value may not hold '%' (a parameter-entity reference is illegal in the internal subset), and every
    // '&' must start a well-formed reference whose character reference is an XML character.
    private void CheckEntityValue(int index, int end)
    {
        while (index < end)
        {
            switch (_text[index])
            {
                case '%':
                    throw Fail();
                case '&':
                    index = ScanReference(index, end, out _, out _);
                    break;
                default:
                    index++;
                    break;
            }
        }
    }

    private void AttlistState(Token token)
    {
        if (token.Kind == Tok.Space)
        {
            return;
        }

        _state = (_state, token.Kind) switch
        {
            (State.Attlist0, Tok.Name or Tok.PrefixedName) => AttlistElement(token),
            (State.Attlist1, Tok.DeclarationClose) => State.InternalSubset,
            (State.Attlist1, Tok.Name or Tok.PrefixedName) => AttributeName(token),
            (State.Attlist2, Tok.Name) when AttributeTypes.Contains(TokenText(token)) => AttributeType(token),
            (State.Attlist2, _) when Keyword(token, "NOTATION") => AttributeType(token, State.Attlist5),
            (State.Attlist2, Tok.OpenParen) => AttributeType(token, State.Attlist3),
            (State.Attlist3, Tok.NameToken or Tok.Name or Tok.PrefixedName) => State.Attlist4,
            (State.Attlist4 or State.Attlist7, Tok.CloseParen) => State.Attlist8,
            (State.Attlist4, Tok.Or) => State.Attlist3,
            (State.Attlist5, Tok.OpenParen) => State.Attlist6,
            (State.Attlist6, Tok.Name) => State.Attlist7,
            (State.Attlist7, Tok.Or) => State.Attlist6,
            (State.Attlist8, Tok.PoundName) => AttributeKeyword(token),
            (State.Attlist8 or State.Attlist9, Tok.Literal) => AttributeValue(token),
            _ => throw Fail(),
        };
    }

    private State AttlistElement(Token name)
    {
        _declElement = TokenText(name);
        return State.Attlist1;
    }

    private State AttributeName(Token name)
    {
        _declAttribute = TokenText(name);
        return State.Attlist2;
    }

    // CDATA is the only type whose values are not normalised; an enumeration or NOTATION type is tokenized.
    private State AttributeType(Token type, State next = State.Attlist8)
    {
        _declAttributeIsCdata = Keyword(type, "CDATA");
        return next;
    }

    private State AttributeKeyword(Token keyword)
    {
        if (SpanEquals(keyword.Start, keyword.End, "IMPLIED") || SpanEquals(keyword.Start, keyword.End, "REQUIRED"))
        {
            if (_keepProcessing)
            {
                DefineAttribute(null);
            }

            return State.Attlist1;
        }

        return SpanEquals(keyword.Start, keyword.End, "FIXED") ? State.Attlist9 : throw Fail();
    }

    private State AttributeValue(Token literal)
    {
        if (_keepProcessing)
        {
            DefineAttribute(NormalizeAttributeValue(literal.Start + 1, literal.End - 1, _declAttributeIsCdata));
        }

        return State.Attlist1;
    }

    // A default is ignored when the element already has a declaration of the attribute with a value; a declaration
    // without one is always recorded (the first recorded declaration decides the attribute's type).
    private void DefineAttribute(string? value)
    {
        if (!_attributeDefaults.TryGetValue(_declElement, out var declared))
        {
            declared = [];
            _attributeDefaults[_declElement] = declared;
        }

        if (value is not null && declared.Exists(entry => string.Equals(entry.Name, _declAttribute, StringComparison.Ordinal)))
        {
            return;
        }

        declared.Add(new AttributeDefault(_declAttribute, _declAttributeIsCdata, value));
    }

    private void ElementState(Token token)
    {
        if (token.Kind == Tok.Space)
        {
            return;
        }

        _state = (_state, token.Kind) switch
        {
            (State.DeclarationClose, Tok.DeclarationClose) => State.InternalSubset,
            (State.Element0, Tok.Name or Tok.PrefixedName) => State.Element1,
            (State.Element1, _) when Keyword(token, "EMPTY") || Keyword(token, "ANY") => State.DeclarationClose,
            (State.Element1, Tok.OpenParen) => OpenGroup(1, State.Element2),
            (State.Element2, Tok.PoundName) when SpanEquals(token.Start, token.End, "PCDATA") => State.Element3,
            (State.Element2 or State.Element6, Tok.OpenParen) => OpenGroup(_groupLevel + 1, State.Element6),
            (State.Element2 or State.Element6, Tok.Name or Tok.PrefixedName or Tok.NameQuestion or Tok.NameAsterisk or Tok.NamePlus)
                => State.Element7,
            (State.Element3, Tok.CloseParen or Tok.CloseParenAsterisk) => State.DeclarationClose,
            (State.Element3 or State.Element5, Tok.Or) => Connector('|', State.Element4),
            (State.Element4, Tok.Name or Tok.PrefixedName) => State.Element5,
            (State.Element5, Tok.CloseParenAsterisk) => State.DeclarationClose,
            (State.Element7, Tok.CloseParen or Tok.CloseParenAsterisk or Tok.CloseParenQuestion or Tok.CloseParenPlus) => CloseGroup(),
            (State.Element7, Tok.Comma) => Connector(',', State.Element6),
            (State.Element7, Tok.Or) => Connector('|', State.Element6),
            _ => throw Fail(),
        };
    }

    private State OpenGroup(int level, State next)
    {
        _groupLevel = level;
        while (_groupConnectors.Count <= level)
        {
            _groupConnectors.Add('\0');
        }

        _groupConnectors[level] = '\0';
        return next;
    }

    private State CloseGroup()
    {
        _groupLevel--;
        return _groupLevel == 0 ? State.DeclarationClose : State.Element7;
    }

    // A group may not mix ',' and '|'.
    private State Connector(char connector, State next)
    {
        var current = _groupConnectors[_groupLevel];
        if (current != '\0' && current != connector)
        {
            throw Fail();
        }

        _groupConnectors[_groupLevel] = connector;
        return next;
    }
}
