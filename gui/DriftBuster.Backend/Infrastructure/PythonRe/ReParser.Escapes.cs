using System.Globalization;

namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>Escape handling: <c>_escape</c> and <c>_class_escape</c>.</summary>
internal static partial class ReParser
{
    private static int? SimpleEscape(int code) => code switch
    {
        'a' => 0x07,
        'b' => 0x08,
        'f' => 0x0C,
        'n' => 0x0A,
        'r' => 0x0D,
        't' => 0x09,
        'v' => 0x0B,
        '\\' => '\\',
        _ => null,
    };

    private static ReCategory? CategoryEscape(int code) => code switch
    {
        'd' => ReCategory.Digit,
        'D' => ReCategory.NotDigit,
        's' => ReCategory.Space,
        'S' => ReCategory.NotSpace,
        'w' => ReCategory.Word,
        'W' => ReCategory.NotWord,
        _ => null,
    };

    /// <summary><c>_class_escape</c>: a literal code point or a category inside a character set.</summary>
    private static ReNode.SetItem ClassEscape(ReTokenizer source, int[] escape)
    {
        var code = escape[1];
        if (SimpleEscape(code) is { } simple)
        {
            return new ReNode.SetItem.Literal(simple);
        }

        if (CategoryEscape(code) is { } category)
        {
            return new ReNode.SetItem.Category(category);
        }

        if (TryCodeEscape(source, escape, out var literal))
        {
            return new ReNode.SetItem.Literal(literal);
        }

        if (OctDigits.Contains((char)code, StringComparison.Ordinal))
        {
            var digits = new List<int>(escape.Skip(1));
            digits.AddRange(source.GetWhile(2, OctDigits));
            var value = Convert.ToInt32(ReTokenizer.Text(digits), 8);
            if (value > 0xFF)
            {
                throw source.Error($"octal escape value \\{ReTokenizer.Text(digits)} outside of range 0-0o377", digits.Count + 1);
            }

            return new ReNode.SetItem.Literal(value);
        }

        if (!IsDigit(code) && !IsAsciiLetter(code))
        {
            return new ReNode.SetItem.Literal(code);
        }

        throw source.Error("bad escape " + TokenText(escape), escape.Length);
    }

    /// <summary><c>_escape</c>: an escape outside a character set.</summary>
    private static ReNode Escape(ReTokenizer source, int[] escape, ReParseState state)
    {
        var code = escape[1];
        switch (code)
        {
            case 'A':
                return new ReNode.At(AtCode.BeginningString);
            case 'b':
                return new ReNode.At(AtCode.Boundary);
            case 'B':
                return new ReNode.At(AtCode.NonBoundary);
            case 'Z':
                return new ReNode.At(AtCode.EndString);
        }

        if (CategoryEscape(code) is { } category)
        {
            return new ReNode.In([new ReNode.SetItem.Category(category)]);
        }

        if (SimpleEscape(code) is { } simple)
        {
            return new ReNode.Literal(simple, negated: false);
        }

        if (TryCodeEscape(source, escape, out var literal))
        {
            return new ReNode.Literal(literal, negated: false);
        }

        if (code == '0')
        {
            var digits = new List<int> { '0' };
            digits.AddRange(source.GetWhile(2, OctDigits));
            return new ReNode.Literal(Convert.ToInt32(ReTokenizer.Text(digits), 8), negated: false);
        }

        if (IsDigit(code))
        {
            return DigitEscape(source, escape, state);
        }

        if (!IsAsciiLetter(code))
        {
            return new ReNode.Literal(code, negated: false);
        }

        throw source.Error("bad escape " + TokenText(escape), escape.Length);
    }

    // \x, \u, \U and \N, shared by both escape forms.
    private static bool TryCodeEscape(ReTokenizer source, int[] escape, out int value)
    {
        value = 0;
        var width = escape[1] switch
        {
            'x' => 2,
            'u' => 4,
            'U' => 8,
            _ => 0,
        };
        if (escape[1] == 'N')
        {
            if (!source.Match('{'))
            {
                throw source.Error("missing {");
            }

            var name = source.GetUntil('}', "character name");
            var nameCodes = ReTokenizer.CodePoints(name);
            if (nameCodes.Any(PythonCharacterData.IsSurrogate))
            {
                // unicodedata.lookup cannot encode the name as UTF-8; the UnicodeEncodeError is a ValueError, which the escape
                // parser turns into "bad escape".
                throw source.Error("bad escape " + TokenText(escape), escape.Length);
            }

            value = UnicodeNames.Lookup(name)
                ?? throw source.Error($"undefined character name {PythonRepr.StrRepr(name)}", nameCodes.Length + 4);
            return true;
        }

        if (width == 0)
        {
            return false;
        }

        var digits = source.GetWhile(width, HexDigits);
        var text = TokenText(escape) + ReTokenizer.Text(digits);
        if (digits.Count != width)
        {
            throw source.Error("incomplete escape " + text, digits.Count + 2);
        }

        value = int.Parse(ReTokenizer.Text(digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (width == 8 && (uint)value > 0x10FFFF)
        {
            throw source.Error("bad escape " + text, digits.Count + 2);
        }

        return true;
    }

    private static ReNode DigitEscape(ReTokenizer source, int[] escape, ReParseState state)
    {
        var digits = new List<int> { escape[1] };
        if (source.Next is { Length: 1 } next && IsDigit(next[0]))
        {
            digits.Add(source.Get()![0]);
            if (OctDigits.Contains((char)digits[0], StringComparison.Ordinal) && OctDigits.Contains((char)digits[1], StringComparison.Ordinal)
                && ReTokenizer.IsIn(source.Next, OctDigits))
            {
                digits.Add(source.Get()![0]);
                var value = Convert.ToInt32(ReTokenizer.Text(digits), 8);
                if (value > 0xFF)
                {
                    throw source.Error($"octal escape value \\{ReTokenizer.Text(digits)} outside of range 0-0o377", digits.Count + 1);
                }

                return new ReNode.Literal(value, negated: false);
            }
        }

        var group = int.Parse(ReTokenizer.Text(digits), CultureInfo.InvariantCulture);
        if (group < state.Groups)
        {
            if (!state.CheckGroup(group))
            {
                throw source.Error("cannot refer to an open group", digits.Count + 1);
            }

            state.CheckLookbehindGroup(group, source);
            return new ReNode.GroupRef(group);
        }

        throw source.Error($"invalid group reference {group}", digits.Count);
    }
}
