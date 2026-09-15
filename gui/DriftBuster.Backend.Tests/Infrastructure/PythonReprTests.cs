using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Every expected string below is the interpreter's own repr()/str() output.</summary>
public sealed class PythonReprTests
{
    [Theory]
    [InlineData(1e22, "1e+22")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(0.5, "0.5")]
    [InlineData(1e16, "1e+16")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(1000.0, "1000.0")]
    [InlineData(0.001, "0.001")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(123.456, "123.456")]
    [InlineData(2.5e-7, "2.5e-07")]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NegativeInfinity, "-inf")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(100.0, "100.0")]
    [InlineData(1e100, "1e+100")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    public void FloatReprMatchesPython(double value, string expected)
    {
        PythonRepr.Float(value).Should().Be(expected);
        PythonRepr.Str(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("", "''")]
    [InlineData("a", "'a'")]
    [InlineData("'", "\"'\"")]
    [InlineData("\"", "'\"'")]
    [InlineData("'\"", "'\\'\"'")]
    [InlineData("\\", "'\\\\'")]
    [InlineData("\t\n\r", "'\\t\\n\\r'")]
    [InlineData("\x00\x1f\x7f", "'\\x00\\x1f\\x7f'")]
    [InlineData("\x80\xa0\xad", "'\\x80\\xa0\\xad'")]
    [InlineData("\u2028\u3000", "'\\u2028\\u3000'")]
    [InlineData("\U0001F600", "'\U0001F600'")]
    [InlineData("\U000E0001", "'\\U000e0001'")]
    [InlineData("\u00e9", "'\u00e9'")]
    [InlineData("a b", "'a b'")]
    [InlineData("ࢗᲉ␧", "'\\u0897\\u1c89\\u2427'")]
    [InlineData("\U00013460\U0001FBEF", "'\\U00013460\\U0001fbef'")]
    public void StrReprMatchesPython(string value, string expected)
    {
        PythonRepr.StrRepr(value).Should().Be(expected);
        PythonRepr.Repr(value).Should().Be(expected);
        PythonRepr.Str(value).Should().Be(value);
    }

    // Built at runtime: xunit's theory-data serialisation replaces a lone surrogate before the test sees it.
    [Fact]
    public void LoneSurrogatesAreEscapedByCodeUnit()
    {
        var value = new string('\ud800', 1);
        PythonRepr.StrRepr(value).Should().Be("'\\ud800'");
        PythonRepr.StrRepr("a" + '\udfff').Should().Be("'a\\udfff'");
        PythonRepr.StrRepr("\U0001F600").Should().Be("'\U0001F600'");
    }

    [Fact]
    public void ScalarsAndContainersUsePythonSpellings()
    {
        PythonRepr.Str(null).Should().Be("None");
        PythonRepr.Str(true).Should().Be("True");
        PythonRepr.Str(false).Should().Be("False");
        PythonRepr.Str(1).Should().Be("1");
        PythonRepr.Str(-2147483649L).Should().Be("-2147483649");
        PythonRepr.Str(BigInteger.Parse("12345678901234567890", System.Globalization.CultureInfo.InvariantCulture)).Should().Be("12345678901234567890");
        PythonRepr.Str(new List<object?> { 1, 2 }).Should().Be("[1, 2]");
        PythonRepr.Str(new List<object?>()).Should().Be("[]");
        var dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = 1,
            ["a"] = new List<object?> { null, false, 2.5 },
            ["q'"] = "b'c",
        };
        PythonRepr.Str(dict).Should().Be("{'b': 1, 'a': [None, False, 2.5], \"q'\": \"b'c\"}");
        PythonRepr.Str(new OrderedDictionary<string, object?>(StringComparer.Ordinal)).Should().Be("{}");

        var act = () => PythonRepr.Repr(new object());
        act.Should().Throw<ArgumentException>();
        var nestedAct = () => PythonRepr.Repr(new List<object?> { 1, new List<object?> { new object() } });
        nestedAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeepContainersAreSpelledWithoutRecursion()
    {
        // str() of the deepest list json.loads can return: CPython spells it out, and so must the port on any thread.
        var depth = PythonJson.MaxNestingDepth;
        PythonJson.TryLoads(new string('[', depth) + new string(']', depth), out var deepList).Should().BeTrue();
        var text = StackProbe.RunOnSmallStack(() => PythonRepr.Str(deepList));
        text.Should().Be(new string('[', depth) + new string(']', depth));

        PythonJson.TryLoads(string.Concat(Enumerable.Repeat("{\"k\": [1, ", 3000)) + "null" + string.Concat(Enumerable.Repeat("]}", 3000)), out var mixed)
            .Should().BeTrue();
        var mixedText = StackProbe.RunOnSmallStack(() => PythonRepr.Repr(mixed));
        mixedText.Should().Be(string.Concat(Enumerable.Repeat("{'k': [1, ", 3000)) + "None" + string.Concat(Enumerable.Repeat("]}", 3000)));
    }
}
