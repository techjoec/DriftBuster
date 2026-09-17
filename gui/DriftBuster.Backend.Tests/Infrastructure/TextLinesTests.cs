using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Line splitting on every line boundary the product recognises.</summary>
public sealed class TextLinesTests
{
    public static TheoryData<string, string[], string[]> Cases => new()
    {
        { "a\nb", ["a", "b"], ["a\n", "b"] },
        { "a\r\nb", ["a", "b"], ["a\r\n", "b"] },
        { "a\rb", ["a", "b"], ["a\r", "b"] },
        { "a\vb", ["a", "b"], ["a\v", "b"] },
        { "a\fb", ["a", "b"], ["a\f", "b"] },
        { "a\u001cb", ["a", "b"], ["a\u001c", "b"] },
        { "a\u001db", ["a", "b"], ["a\u001d", "b"] },
        { "a\u001eb", ["a", "b"], ["a\u001e", "b"] },
        { "a\u0085b", ["a", "b"], ["a\u0085", "b"] },
        { "a\u2028b", ["a", "b"], ["a\u2028", "b"] },
        { "a\u2029b", ["a", "b"], ["a\u2029", "b"] },
        { "a\n", ["a"], ["a\n"] },
        { "", [], [] },
        { "\n", [""], ["\n"] },
        { "a\n\nb", ["a", "", "b"], ["a\n", "\n", "b"] },
        { "a\r\n\rb", ["a", "", "b"], ["a\r\n", "\r", "b"] },
        { "\n\n", ["", ""], ["\n", "\n"] },
        { "a", ["a"], ["a"] },
        { "a\n\rb", ["a", "", "b"], ["a\n", "\r", "b"] },
        { "a\u0085\u2028", ["a", ""], ["a\u0085", "\u2028"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SplitLinesBreaksOnEveryLineBoundary(string text, string[] expected, string[] expectedKeepEnds)
    {
        TextLines.SplitLines(text).Should().Equal(expected);
        TextLines.SplitLines(text, keepEnds: true).Should().Equal(expectedKeepEnds);
    }

    [Fact]
    public void SplitLinesDoesNotSplitOnOtherWhitespace()
    {
        TextLines.SplitLines("a b\tc d e").Should().Equal("a b\tc d e");
        TextLines.IsLineBoundary(' ').Should().BeFalse();
        TextLines.IsLineBoundary('\n').Should().BeTrue();
    }
}
