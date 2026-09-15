using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Acceptance and value shapes of <see cref="PythonJson"/>; every outcome was checked against json.loads.</summary>
public sealed class PythonJsonTests
{
    private static object? Loads(string text)
    {
        PythonJson.TryLoads(text, out var value).Should().BeTrue(text);
        return value;
    }

    [Theory]
    [InlineData("[1, 2.0, -3, 1E2, 1e, 1.e1]")]
    [InlineData("[1e]")]
    [InlineData("[1.]")]
    [InlineData("1 2")]
    [InlineData("[01]")]
    [InlineData("nul")]
    [InlineData("-")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("\"\x1f\"")]
    [InlineData("\x0b5")]
    [InlineData("\ufeff{}")]
    [InlineData("")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{1: 2}")]
    [InlineData("\"\\x41\"")]
    [InlineData("\"\\u12\"")]
    [InlineData("{\"registry_scan\": {\"token\": \"App\"")]
    public void RejectsWhatTheCDecoderRejects(string text)
    {
        PythonJson.TryLoads(text, out var value).Should().BeFalse(text);
        value.Should().BeNull();
    }

    [Fact]
    public void DuplicateKeysKeepTheFirstSlotWithTheLastValue()
    {
        var dict = (OrderedDictionary<string, object?>)Loads("{\"a\":1,\"b\":3,\"a\":2}")!;
        dict.Keys.Should().Equal("a", "b");
        dict["a"].Should().Be(2);
        dict["b"].Should().Be(3);
    }

    [Fact]
    public void ScalarsHaveTheirPythonTypes()
    {
        Loads(" 1 ").Should().Be(1);
        Loads("-0").Should().Be(0);
        Loads("2147483648").Should().Be(2147483648L);
        Loads("12345678901234567890").Should().Be(BigInteger.Parse("12345678901234567890", System.Globalization.CultureInfo.InvariantCulture));
        Loads("1e3").Should().Be(1000.0);
        Loads("10.0").Should().Be(10.0);
        Loads("1e400").Should().Be(double.PositiveInfinity);
        Loads("-Infinity").Should().Be(double.NegativeInfinity);
        double.IsNaN((double)Loads("NaN")!).Should().BeTrue();
        Loads("true").Should().Be(true);
        Loads("false").Should().Be(false);
        Loads("null").Should().BeNull();
        Loads("\t\n\r 5").Should().Be(5);
        Loads("\"\x7f\"").Should().Be("\x7f");
    }

    [Fact]
    public void StringsDecodeEscapesAndKeepLoneSurrogates()
    {
        Loads("\"\\ud83d\\ude00\"").Should().Be("\U0001F600");
        Loads("\"\\ud83d\\u0041\"").Should().Be("\ud83dA");
        Loads("[\"a\\/b\\u0041\\\"\\\\\\b\\f\\n\\r\\t\"]").Should().BeEquivalentTo(new List<object?> { "a/bA\"\\\b\f\n\r\t" });
    }

    [Fact]
    public void ContainersNestAndEmptyOnesClose()
    {
        var dict = (OrderedDictionary<string, object?>)Loads("{\"a\" : [ ] , \"b\":{}, \"c\": [1, [2, {\"d\": null}]]}")!;
        dict.Keys.Should().Equal("a", "b", "c");
        ((List<object?>)dict["a"]!).Should().BeEmpty();
        ((OrderedDictionary<string, object?>)dict["b"]!).Should().BeEmpty();
        var c = (List<object?>)dict["c"]!;
        c[0].Should().Be(1);
        var inner = (List<object?>)c[1]!;
        inner[0].Should().Be(2);
        ((OrderedDictionary<string, object?>)inner[1]!)["d"].Should().BeNull();
        ((List<object?>)Loads("[]")!).Should().BeEmpty();
        ((OrderedDictionary<string, object?>)Loads("{}")!).Should().BeEmpty();
    }

    [Fact]
    public void IntegerLiteralsRespectTheDigitLimit()
    {
        var atLimit = "[" + new string('1', PythonJson.MaxIntDigits) + "]";
        var list = (List<object?>)Loads(atLimit)!;
        list[0].Should().BeOfType<BigInteger>();
        PythonJson.TryLoads("[" + new string('1', PythonJson.MaxIntDigits + 1) + "]", out _).Should().BeFalse();
        PythonJson.TryLoads("[-" + new string('1', PythonJson.MaxIntDigits) + "]", out _).Should().BeTrue();
    }

    [Fact]
    public void NestingStopsWhereTheCScannerRaisesRecursionError()
    {
        // json.loads decodes 9998 nested containers and raises RecursionError at 9999 (CPython 3.13, 64-bit Linux).
        static string Lists(int depth) => new string('[', depth) + new string(']', depth);
        static string Objects(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);

        var parsed = StackProbe.RunOnSmallStack(() => PythonJson.TryLoads(Lists(PythonJson.MaxNestingDepth), out _));
        parsed.Should().BeTrue();
        PythonJson.TryLoads(Lists(PythonJson.MaxNestingDepth + 1), out _).Should().BeFalse();
        PythonJson.TryLoads(Objects(PythonJson.MaxNestingDepth), out _).Should().BeTrue();
        PythonJson.TryLoads(Objects(PythonJson.MaxNestingDepth + 1), out _).Should().BeFalse();
        PythonJson.TryLoads("[" + Lists(PythonJson.MaxNestingDepth - 1) + ", 1]", out _).Should().BeTrue();
        PythonJson.TryLoads("[1, " + Lists(PythonJson.MaxNestingDepth) + "]", out _).Should().BeFalse();
        PythonJson.TryLoads(new string('[', 60000) + new string(']', 60000), out _).Should().BeFalse();
    }

    // CPython 3.13: json.loads raises these past its limits, at the first failure in text order; a syntax error before the limit is a
    // JSONDecodeError (false).
    [Fact]
    public void TryLoadsOrRaiseLimitsRaisesTheInterpreterLimitErrors()
    {
        var digits = () => PythonJson.TryLoadsOrRaiseLimits("[-" + new string('1', PythonJson.MaxIntDigits + 1) + "]", out _);
        digits.Should().Throw<PythonValueException>().WithMessage(
            "Exceeds the limit (4300 digits) for integer string conversion: value has 4301 digits; use sys.set_int_max_str_digits() to increase the limit");
        var lists = () => PythonJson.TryLoadsOrRaiseLimits(new string('[', PythonJson.MaxNestingDepth + 1), out _);
        lists.Should().Throw<PythonRecursionException>().WithMessage("maximum recursion depth exceeded while decoding a JSON array from a unicode string");
        var objects = () => PythonJson.TryLoadsOrRaiseLimits(string.Concat(Enumerable.Repeat("{\"a\": ", PythonJson.MaxNestingDepth + 1)), out _);
        objects.Should().Throw<PythonRecursionException>().WithMessage("maximum recursion depth exceeded while decoding a JSON object from a unicode string");

        PythonJson.TryLoadsOrRaiseLimits("[x, " + new string('1', PythonJson.MaxIntDigits + 1) + "]", out _).Should().BeFalse();
        PythonJson.TryLoadsOrRaiseLimits("[1]", out var value).Should().BeTrue();
        value.Should().BeEquivalentTo(new List<object?> { 1 });
        PythonJson.TryLoads("[" + new string('1', PythonJson.MaxIntDigits + 1) + "]", out _).Should().BeFalse();
    }

    // json.decoder._CONSTANTS: every NaN literal decodes to one float object, so a NaN is found by identity in a set or as a dict key.
    [Fact]
    public void EveryNaNLiteralIsOneObject()
    {
        var first = (List<object?>)Loads("[NaN, NaN]")!;
        var second = new List<object?> { Loads("NaN") };
        ReferenceEquals(first[0], first[1]).Should().BeTrue();
        ReferenceEquals(first[0], second[0]).Should().BeTrue();
        PythonValues.Equal(first[0], second[0]).Should().BeTrue();
        PythonValues.Equal(first[0], double.NaN).Should().BeFalse();
        new HashSet<object?>(first, PythonValues.HashKeys).Should().ContainSingle();
    }
}
