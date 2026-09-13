namespace DriftBuster.Backend.Infrastructure.PythonRe;

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
    internal static CodePointSet NodeSet(ReNode node, PythonReFlags flags) => node switch
    {
        ReNode.Literal literal => LiteralSet(literal, flags),
        ReNode.In set => InSet(set, flags),
        ReNode.Any => AnySet(flags),
        _ => throw new ArgumentException("not a character node", nameof(node)),
    };

    private static CodePointSet LiteralSet(ReNode.Literal literal, PythonReFlags flags)
    {
        var set = CaseInsensitiveLiteral(literal.Code, flags) ?? CodePointSet.Single(literal.Code);
        return literal.Negated ? set.Complement() : set;
    }

    private static CodePointSet? CaseInsensitiveLiteral(int code, PythonReFlags flags)
    {
        if (!flags.HasFlag(PythonReFlags.IgnoreCase))
        {
            return null;
        }

        if (flags.HasFlag(PythonReFlags.Ascii))
        {
            return PythonCharacterData.AsciiIsCased(code)
                ? PythonCharacterData.AsciiLowerPreimage(CodePointSet.Single(PythonCharacterData.AsciiLower(code)))
                : null;
        }

        if (!PythonCharacterData.IsCased(code))
        {
            return null;
        }

        var lower = PythonCharacterData.Lower(code);
        var targets = PythonCharacterData.ExtraCases.TryGetValue(lower, out var extra)
            ? CodePointSet.FromCodePoints(extra.Append(lower))
            : CodePointSet.Single(lower);
        return PythonCharacterData.LowerPreimage(targets);
    }

    private static CodePointSet AnySet(PythonReFlags flags)
        => flags.HasFlag(PythonReFlags.DotAll) ? CodePointSet.All : NotNewline;

    private static readonly CodePointSet NotNewline = CodePointSet.Single('\n').Complement();

    private static CodePointSet InSet(ReNode.In node, PythonReFlags flags)
    {
        var ignoreCase = flags.HasFlag(PythonReFlags.IgnoreCase);
        var ascii = flags.HasFlag(PythonReFlags.Ascii);
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
            membership = ascii ? PythonCharacterData.AsciiLowerPreimage(membership) : PythonCharacterData.LowerPreimage(membership);
        }

        return negate ? membership.Complement() : membership;
    }

    // A literal set member under IGNORECASE: its lowercase (bitmap or kept literal) plus extra cases in the bitmap.
    private static CodePointSet FoldedLiteral(int code, bool ascii, ref bool hasCased)
    {
        var lower = ascii ? PythonCharacterData.AsciiLower(code) : PythonCharacterData.Lower(code);
        hasCased |= ascii ? PythonCharacterData.AsciiIsCased(lower) : PythonCharacterData.IsCased(lower);
        var set = CodePointSet.Single(lower);
        if (!ascii && lower <= 0xFFFF && PythonCharacterData.ExtraCases.TryGetValue(lower, out var extra))
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
                : bmp.CodePoints().Any(PythonCharacterData.IsCased);
            return lowered;
        }

        hasCased = true;
        var whole = CodePointSet.Range(range.Low, range.High);
        var tail = ascii ? whole : whole.Union(PythonCharacterData.UpperPreimage(whole));
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
            var lower = PythonCharacterData.Lower(code);
            lowered.Add(lower);
            if (PythonCharacterData.ExtraCases.TryGetValue(lower, out var extra))
            {
                lowered.UnionWith(extra);
            }
        }

        return CodePointSet.FromCodePoints(lowered);
    }

    private static CodePointSet CategorySet(ReCategory category, bool ascii) => category switch
    {
        ReCategory.Digit => ascii ? PythonCharacterData.AsciiDigit : PythonCharacterData.Digit,
        ReCategory.NotDigit => (ascii ? PythonCharacterData.AsciiDigit : PythonCharacterData.Digit).Complement(),
        ReCategory.Space => ascii ? PythonCharacterData.AsciiSpace : PythonCharacterData.Space,
        ReCategory.NotSpace => (ascii ? PythonCharacterData.AsciiSpace : PythonCharacterData.Space).Complement(),
        ReCategory.Word => ascii ? PythonCharacterData.AsciiWord : PythonCharacterData.Word,
        _ => (ascii ? PythonCharacterData.AsciiWord : PythonCharacterData.Word).Complement(),
    };
}
