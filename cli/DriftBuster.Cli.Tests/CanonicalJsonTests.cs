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
    public void Objects_sort_keys_by_code_point_and_use_python_separators()
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

    // json.dumps sorts str keys by code point: U+10400 follows U+E000 and U+FF41, where UTF-16 ordinal puts it first.
    [Fact]
    public void Objects_sort_astral_keys_after_every_bmp_key()
    {
        var value = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["\uff41"] = 1,
            ["\U00010400"] = 2,
            ["\ue000"] = 3,
            ["b"] = 4,
        };

        CanonicalJson.Serialize(value).Should().Be("{\"b\": 4, \"\ue000\": 3, \"\uff41\": 1, \"\U00010400\": 2}");
    }

    [Fact]
    public void Unknown_values_fall_back_to_their_string_form()
    {
        CanonicalJson.Serialize(new Uri("https://example.test/a")).Should().Be("\"https://example.test/a\"");
        CanonicalJson.Serialize(DayOfWeek.Monday).Should().Be("\"Monday\"");
    }

    // py_dump.py rewrites every unpaired surrogate to the literal text \uXXXX before json.dumps escapes the backslash.
    [Fact]
    public void Lone_surrogates_are_written_as_literal_escapes_on_both_sides()
    {
        CanonicalJson.Serialize("\ud83d").Should().Be("\"\\\\ud83d\"");
        CanonicalJson.Serialize("a\ude00b").Should().Be("\"a\\\\ude00b\"");
        CanonicalJson.Serialize("\ud83d\ude00").Should().Be("\"\ud83d\ude00\"");
        CanonicalJson.Serialize("\ud83dx\ud83d\ude00").Should().Be("\"\\\\ud83dx\ud83d\ude00\"");
        CanonicalJson.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal) { ["\ud83d"] = 1, ["a"] = 2 })
            .Should().Be("{\"\\\\ud83d\": 1, \"a\": 2}");
        CanonicalJson.EscapeLoneSurrogates("plain").Should().BeSameAs("plain");
    }
}
