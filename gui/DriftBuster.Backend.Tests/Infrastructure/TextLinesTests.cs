using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Line splitting on the line boundaries .NET recognises.</summary>
public sealed class TextLinesTests
{
    public static TheoryData<string, string[]> Cases => new()
    {
        { "a\nb", ["a", "b"] },
        { "a\r\nb", ["a", "b"] },
        { "a\rb", ["a", "b"] },
        { "a\fb", ["a", "b"] },
        { "a\u0085b", ["a", "b"] },
        { "a\u2028b", ["a", "b"] },
        { "a\u2029b", ["a", "b"] },
        { "a\vb", ["a\vb"] },
        { "a\u001cb", ["a\u001cb"] },
        { "a\n", ["a"] },
        { "", [] },
        { "\n", [""] },
        { "a\n\nb", ["a", "", "b"] },
        { "a\r\n\rb", ["a", "", "b"] },
        { "\n\n", ["", ""] },
        { "a", ["a"] },
        { "a\n\rb", ["a", "", "b"] },
        { "a b\tc", ["a b\tc"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SplitLinesBreaksOnDotNetLineBoundaries(string text, string[] expected)
        => TextLines.SplitLines(text).Should().Equal(expected);
}
