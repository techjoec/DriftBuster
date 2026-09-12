using DriftBuster.Cli;

namespace DriftBuster.Cli.Tests;

/// <summary>Every expectation here is the literal output of Python json.dumps(value, sort_keys=True, ensure_ascii=False).</summary>
public sealed class CanonicalJsonTests
{
    [Fact]
    public void Scalars_match_python()
    {
        CanonicalJson.Serialize(null).Should().Be("null");
        CanonicalJson.Serialize(true).Should().Be("true");
        CanonicalJson.Serialize(false).Should().Be("false");
        CanonicalJson.Serialize(4).Should().Be("4");
        CanonicalJson.Serialize(-7L).Should().Be("-7");
        CanonicalJson.Serialize(0.8).Should().Be("0.8");
        CanonicalJson.Serialize(0.68).Should().Be("0.68");
        CanonicalJson.Serialize(1.0).Should().Be("1.0");
        CanonicalJson.Serialize(0.0).Should().Be("0.0");
        CanonicalJson.Serialize(1e-05).Should().Be("1e-05");
        CanonicalJson.Serialize(1e16).Should().Be("1e+16");
        CanonicalJson.Serialize(1e15).Should().Be("1000000000000000.0");
        CanonicalJson.Serialize(0.0001).Should().Be("0.0001");
        CanonicalJson.Serialize(0.00001234).Should().Be("1.234e-05");
        CanonicalJson.Serialize(-2.5e+20).Should().Be("-2.5e+20");
        CanonicalJson.Serialize(-0.0).Should().Be("-0.0");
        CanonicalJson.Serialize(1e100).Should().Be("1e+100");
        CanonicalJson.Serialize(0.1 + 0.2).Should().Be("0.30000000000000004");
        CanonicalJson.Serialize(5e-324).Should().Be("5e-324");
        CanonicalJson.Serialize(123456789.5).Should().Be("123456789.5");
        CanonicalJson.Serialize(1.5f).Should().Be("1.5");
        CanonicalJson.Serialize(2.5m).Should().Be("2.5");
        CanonicalJson.Serialize(double.NaN).Should().Be("NaN");
        CanonicalJson.Serialize(double.PositiveInfinity).Should().Be("Infinity");
        CanonicalJson.Serialize(double.NegativeInfinity).Should().Be("-Infinity");
    }

    [Fact]
    public void Strings_escape_like_python_with_ensure_ascii_false()
    {
        CanonicalJson.Serialize("é\u0001\n\r\t\b\f\"\\\u007f\u2028 ✓").Should().Be("\"é\\u0001\\n\\r\\t\\b\\f\\\"\\\\\u007f\u2028 ✓\"");
        CanonicalJson.Serialize(string.Empty).Should().Be("\"\"");
        CanonicalJson.Serialize("</script>&'").Should().Be("\"</script>&'\"");
    }

    [Fact]
    public void Objects_sort_keys_ordinally_and_use_python_separators()
    {
        var value = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = 1.0,
            ["a"] = 0.8,
            ["Z"] = new List<object?> { 1, "two", null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["y"] = false, ["x"] = new List<string>() } },
            ["_"] = new string[] { "s" },
        };

        CanonicalJson.Serialize(value).Should().Be("{\"Z\": [1, \"two\", null, {\"x\": [], \"y\": false}], \"_\": [\"s\"], \"a\": 0.8, \"b\": 1.0}");
        CanonicalJson.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)).Should().Be("{}");
        CanonicalJson.Serialize(Array.Empty<int>()).Should().Be("[]");
    }

    [Fact]
    public void Unknown_values_fall_back_to_their_string_form()
    {
        CanonicalJson.Serialize(new Uri("https://example.test/a")).Should().Be("\"https://example.test/a\"");
        CanonicalJson.Serialize(DayOfWeek.Monday).Should().Be("\"Monday\"");
    }
}
