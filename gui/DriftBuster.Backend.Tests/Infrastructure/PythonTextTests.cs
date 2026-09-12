using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Truth tables generated from CPython 3.13 str.isspace, str.strip, str.split, re \w and str.upper.</summary>
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
