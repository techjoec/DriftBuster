using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Truth tables generated from CPython 3.13 str.isspace, str.strip, str.split, re \w, str.lower and str.upper.</summary>
public sealed class PythonTextTests
{
    // Every code point for which str.isspace() is true.
    private static readonly int[] PythonSpaces =
    [
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x85, 0xA0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
    ];

    [Fact]
    public void IsSpaceMatchesStrIsspaceOverTheBasicMultilingualPlane()
    {
        var expected = new HashSet<int>(PythonSpaces);
        for (var code = 0; code <= char.MaxValue; code++)
        {
            PythonText.IsSpace((char)code).Should().Be(expected.Contains(code), $"U+{code:X4}");
        }
    }

    [Fact]
    public void IsSpaceDiffersFromCharIsWhiteSpaceOnlyOnTheInformationSeparators()
    {
        var differences = Enumerable.Range(0, char.MaxValue + 1)
            .Where(code => PythonText.IsSpace((char)code) != char.IsWhiteSpace((char)code))
            .ToList();
        differences.Should().Equal(0x1C, 0x1D, 0x1E, 0x1F);
    }

    [Theory]
    [InlineData("  a b  ", "a b", "a b  ")]
    [InlineData("\u001F\u001Cx\u001D\u001E", "x", "x\u001D\u001E")]
    [InlineData("\u3000\u2028text\u2029", "text", "text\u2029")]
    [InlineData("", "", "")]
    [InlineData("\t\n", "", "")]
    [InlineData("\u200bx", "\u200bx", "\u200bx")]
    public void StripAndStripStartMatchPython(string text, string stripped, string leftStripped)
    {
        PythonText.Strip(text).Should().Be(stripped);
        PythonText.StripStart(text).Should().Be(leftStripped);
    }

    [Fact]
    public void SplitMatchesStrSplit()
    {
        PythonText.Split(" a\u001Fb\u3000\u2028c  ").Should().Equal("a", "b", "c");
        PythonText.Split("").Should().BeEmpty();
        PythonText.Split("\u001C\u001D").Should().BeEmpty();
        PythonText.Split("one").Should().Equal("one");
    }

    [Theory]
    [InlineData('a', true)]
    [InlineData('_', true)]
    [InlineData('5', true)]
    [InlineData('\u00B2', true)]   // No
    [InlineData('\u216B', true)]   // Nl
    [InlineData('\u8A2D', true)]  // Lo
    [InlineData('\u0303', false)] // Mn: .NET \w yes, Python \w no
    [InlineData('\u203F', false)] // Pc: .NET \w yes, Python \w no
    [InlineData('-', false)]
    [InlineData(' ', false)]
    [InlineData('\u001F', false)]
    public void IsWordRuneMatchesPythonWordClass(char ch, bool expected)
    {
        PythonText.IsWordRune(new Rune(ch)).Should().Be(expected);
    }

    [Fact]
    public void IsWordRuneClassifiesAstralCodePoints()
    {
        PythonText.IsWordRune(new Rune(0x1D400)).Should().BeTrue();   // MATHEMATICAL BOLD CAPITAL A, Lu
        PythonText.IsWordRune(new Rune(0x1F600)).Should().BeFalse();  // GRINNING FACE, So
        PythonText.IsWordRune(new Rune(0x1D7CE)).Should().BeTrue();   // MATHEMATICAL BOLD DIGIT ZERO, Nd
    }

    // Values are str.lower() on CPython 3.13: the U+0130 expansion and the Final_Sigma context (a cased code point
    // before and none after, skipping case-ignorables such as combining marks, modifier letters and "." or "'").
    [Theory]
    [InlineData("", "")]
    [InlineData("SETTINGS.INI", "settings.ini")]
    [InlineData("\u0130", "i\u0307")]
    [InlineData("SETT\u0130NGS.\u0130N\u0130", "setti\u0307ngs.i\u0307ni\u0307")]
    [InlineData("\u01C5", "\u01C6")]
    [InlineData("\u03A3", "\u03C3")]
    [InlineData("\u0391\u03A3", "\u03B1\u03C2")]
    [InlineData("\u0391\u03A3\u0391", "\u03B1\u03C3\u03B1")]
    [InlineData("\u0391.\u03A3", "\u03B1.\u03C2")]
    [InlineData("\u0391\u03A3.\u0392", "\u03B1\u03C3.\u03B2")]
    [InlineData("A\u0345 \u03A3", "a\u0345 \u03C3")]
    [InlineData("\u03A3\u03A3", "\u03C3\u03C2")]
    [InlineData("\u02B0\u03A3", "\u02B0\u03C3")]
    [InlineData("\u216F\u03A3", "\u217F\u03C2")]
    [InlineData("\u0391\u03A3\u0307", "\u03B1\u03C2\u0307")]
    [InlineData("\u24B6\u0301\u03A3", "\u24D0\u0301\u03C2")]
    [InlineData("\U0001D400\u03A3", "\U0001D400\u03C2")]
    [InlineData("\U0001F130\u03A3\U0001F600", "\U0001F130\u03C2\U0001F600")]
    public void LowerMatchesStrLower(string text, string expected)
    {
        PythonText.Lower(text).Should().Be(expected);
    }

    // str.lower() leaves a lone surrogate (category Cs) where it is; a paired one is a code point of its own.
    [Theory]
    [InlineData("\ud83d", "\ud83d")]
    [InlineData("\ude00", "\ude00")]
    [InlineData("A\ud83dB", "a\ud83db")]
    [InlineData("\ud83d\ud83d", "\ud83d\ud83d")]
    [InlineData("\ude00\ud83d", "\ude00\ud83d")]
    [InlineData("\u0391\u03a3\ud83d", "\u03b1\u03c2\ud83d")]
    [InlineData("\ud83d\ude00", "\ud83d\ude00")]
    public void LowerKeepsUnpairedSurrogates(string text, string expected)
    {
        PythonText.Lower(text).Should().Be(expected);
    }

    [Fact]
    public void LowerDiffersFromToLowerInvariantOnlyOnDottedIAndFinalSigma()
    {
        for (var code = 0; code <= 0x10FFFF; code++)
        {
            if (code is (>= 0xD800 and <= 0xDFFF) or 0x0130 or 0x03A3)
            {
                continue;
            }

            var text = char.ConvertFromUtf32(code);
            PythonText.Lower(text).Should().Be(text.ToLowerInvariant(), $"U+{code:X4}");
        }
    }

    [Theory]
    [InlineData("\u0130", "\u0130")]
    [InlineData("\u0391\u03A3", "\u03B1\u03C3")]
    public void ToLowerInvariantIsNotStrLower(string text, string invariant)
    {
        text.ToLowerInvariant().Should().Be(invariant);
        PythonText.Lower(text).Should().NotBe(invariant);
    }

    [Theory]
    [InlineData("\u00DF", "SS")]
    [InlineData("\uFB01", "FI")]
    [InlineData("\u01F0", "J\u030C")]
    [InlineData("\u1FB3", "\u0391\u0399")]
    [InlineData("a", "A")]
    [InlineData("\u0131", "I")]
    [InlineData("\u01C6", "\u01C4")]
    [InlineData("\U0001D41A", "\U0001D41A")] // mathematical letters carry no case mapping
    [InlineData("\U00010428", "\U00010400")] // DESERET SMALL LETTER LONG I
    [InlineData("1", "1")]
    public void UpperMatchesStrUpper(string codePoint, string expected)
    {
        PythonText.Upper(Rune.GetRuneAt(codePoint, 0)).Should().Be(expected);
    }
}
