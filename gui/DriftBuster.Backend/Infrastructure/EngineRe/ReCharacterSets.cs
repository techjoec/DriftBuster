namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// The code points one character-consuming node matches, following <c>re._compiler._compile</c> and
/// <c>_optimize_charset</c> and the <c>_sre</c> opcodes they select.
/// </summary>
/// <remarks>
/// Under IGNORECASE a literal that has case becomes <c>LITERAL_UNI_IGNORE lower(c)</c> (matching every code point whose
/// lowercase is <c>lower(c)</c>) or, when <c>lower(c)</c> has extra cases, <c>IN_UNI_IGNORE</c> over it and them. A set with
/// any cased member becomes <c>IN_UNI_IGNORE</c>, which lowers the subject code point before testing it: the BMP members are
/// lowered (plus extra cases) into the bitmap, a non-BMP literal is kept lowered, a range reaching past U+FFFF becomes
/// <c>RANGE_UNI_IGNORE</c> (in the range, or its uppercase in the range) and categories test the lowered code point. With
/// the ASCII flag the same shapes use the ASCII lowercase and no extra cases, and a range past U+FFFF stays a plain
/// <c>RANGE</c>.
/// </remarks>
internal static class ReCharacterSets
{
    /// <summary>The code points a single character-consuming node matches under <paramref name="flags"/>.</summary>
    internal static CodePointSet NodeSet(ReNode node, EngineReFlags flags) => node switch
    {
        ReNode.Literal literal => LiteralSet(literal, flags),
        ReNode.In set => InSet(set, flags),
        ReNode.Any => AnySet(flags),
        _ => throw new ArgumentException("not a character node", nameof(node)),
    };

    private static CodePointSet LiteralSet(ReNode.Literal literal, EngineReFlags flags)
    {
        var set = CaseInsensitiveLiteral(literal.Code, flags) ?? CodePointSet.Single(literal.Code);
        return literal.Negated ? set.Complement() : set;
    }

    private static CodePointSet? CaseInsensitiveLiteral(int code, EngineReFlags flags)
    {
        if (!flags.HasFlag(EngineReFlags.IgnoreCase))
        {
            return null;
        }

        if (flags.HasFlag(EngineReFlags.Ascii))
        {
            return EngineCharacterData.AsciiIsCased(code)
                ? EngineCharacterData.AsciiLowerPreimage(CodePointSet.Single(EngineCharacterData.AsciiLower(code)))
                : null;
        }

        if (!EngineCharacterData.IsCased(code))
        {
            return null;
        }

        var lower = EngineCharacterData.Lower(code);
        var targets = EngineCharacterData.ExtraCases.TryGetValue(lower, out var extra)
            ? CodePointSet.FromCodePoints(extra.Append(lower))
            : CodePointSet.Single(lower);
        return EngineCharacterData.LowerPreimage(targets);
    }

    private static CodePointSet AnySet(EngineReFlags flags)
        => flags.HasFlag(EngineReFlags.DotAll) ? CodePointSet.All : NotNewline;

    private static readonly CodePointSet NotNewline = CodePointSet.Single('\n').Complement();

    private static CodePointSet InSet(ReNode.In node, EngineReFlags flags)
    {
        var ignoreCase = flags.HasFlag(EngineReFlags.IgnoreCase);
        var ascii = flags.HasFlag(EngineReFlags.Ascii);
        var members = new List<CodePointSet>();
        var negate = false;
        var hasCased = false;
        foreach (var item in node.Items)
        {
            switch (item)
            {
                case ReNode.SetItem.Negate:
                    negate = !negate;
                    break;
                case ReNode.SetItem.Literal literal:
                    members.Add(ignoreCase ? FoldedLiteral(literal.Code, ascii, ref hasCased) : CodePointSet.Single(literal.Code));
                    break;
                case ReNode.SetItem.Range range:
                    members.Add(ignoreCase ? FoldedRange(range, ascii, ref hasCased) : CodePointSet.Range(range.Low, range.High));
                    break;
                case ReNode.SetItem.Category category:
                    members.Add(CategorySet(category.Code, ascii));
                    break;
            }
        }

        var membership = members.Aggregate(CodePointSet.Empty, (accumulated, member) => accumulated.Union(member));
        if (hasCased)
        {
            membership = ascii ? EngineCharacterData.AsciiLowerPreimage(membership) : EngineCharacterData.LowerPreimage(membership);
        }

        return negate ? membership.Complement() : membership;
    }

    // A literal set member under IGNORECASE: its lowercase (bitmap or kept literal) plus extra cases in the bitmap.
    private static CodePointSet FoldedLiteral(int code, bool ascii, ref bool hasCased)
    {
        var lower = ascii ? EngineCharacterData.AsciiLower(code) : EngineCharacterData.Lower(code);
        hasCased |= ascii ? EngineCharacterData.AsciiIsCased(lower) : EngineCharacterData.IsCased(lower);
        var set = CodePointSet.Single(lower);
        if (!ascii && lower <= 0xFFFF && EngineCharacterData.ExtraCases.TryGetValue(lower, out var extra))
        {
            set = set.Union(CodePointSet.FromCodePoints(extra));
        }

        return set;
    }

    // A range set member under IGNORECASE: the BMP part lowered into the bitmap (with extra cases); a range reaching past
    // U+FFFF additionally becomes RANGE_UNI_IGNORE (Unicode) or a plain RANGE (ASCII) and marks the set as cased.
    private static CodePointSet FoldedRange(ReNode.SetItem.Range range, bool ascii, ref bool hasCased)
    {
        var bmp = CodePointSet.Range(range.Low, Math.Min(range.High, 0xFFFF));
        var lowered = ascii ? LowerAscii(bmp) : LowerUnicode(bmp);
        if (range.High <= 0xFFFF)
        {
            hasCased |= ascii
                ? !bmp.Intersect(CodePointSet.FromRanges([('A', 'Z'), ('a', 'z')])).IsEmpty
                : bmp.CodePoints().Any(EngineCharacterData.IsCased);
            return lowered;
        }

        hasCased = true;
        var whole = CodePointSet.Range(range.Low, range.High);
        var tail = ascii ? whole : whole.Union(EngineCharacterData.UpperPreimage(whole));
        return lowered.Union(tail);
    }

    private static CodePointSet LowerAscii(CodePointSet bmp)
    {
        var upper = CodePointSet.Range('A', 'Z');
        var shifted = bmp.Intersect(upper).CodePoints().Select(code => code + 32);
        return bmp.Except(upper).Union(CodePointSet.FromCodePoints(shifted));
    }

    private static CodePointSet LowerUnicode(CodePointSet bmp)
    {
        var lowered = new HashSet<int>();
        foreach (var code in bmp.CodePoints())
        {
            var lower = EngineCharacterData.Lower(code);
            lowered.Add(lower);
            if (EngineCharacterData.ExtraCases.TryGetValue(lower, out var extra))
            {
                lowered.UnionWith(extra);
            }
        }

        return CodePointSet.FromCodePoints(lowered);
    }

    private static CodePointSet CategorySet(ReCategory category, bool ascii) => category switch
    {
        ReCategory.Digit => ascii ? EngineCharacterData.AsciiDigit : EngineCharacterData.Digit,
        ReCategory.NotDigit => (ascii ? EngineCharacterData.AsciiDigit : EngineCharacterData.Digit).Complement(),
        ReCategory.Space => ascii ? EngineCharacterData.AsciiSpace : EngineCharacterData.Space,
        ReCategory.NotSpace => (ascii ? EngineCharacterData.AsciiSpace : EngineCharacterData.Space).Complement(),
        ReCategory.Word => ascii ? EngineCharacterData.AsciiWord : EngineCharacterData.Word,
        _ => (ascii ? EngineCharacterData.AsciiWord : EngineCharacterData.Word).Complement(),
    };
}
