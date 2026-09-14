using System.Globalization;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonBuiltins"/> against CPython 3.13: <c>int()</c> and <c>float()</c> of <c>json.loads</c> values (the JSON text is
/// the first column), with Python's result or exception type and message.
/// </summary>
public sealed class PythonBuiltinsTests
{
    public static TheoryData<string, string, string> IntCases() => new()
    {
        { "\"abc\"", "ValueError", "invalid literal for int() with base 10: 'abc'" },
        { "\" 42 \"", "ok", "42" },
        { "\"4_2\"", "ok", "42" },
        { "\"\\u0664\\u0662\"", "ok", "42" },
        { "\"+7\"", "ok", "7" },
        { "\"-0\"", "ok", "0" },
        { "\"2.9\"", "ValueError", "invalid literal for int() with base 10: '2.9'" },
        { "\"\"", "ValueError", "invalid literal for int() with base 10: ''" },
        { "\"1__2\"", "ValueError", "invalid literal for int() with base 10: '1__2'" },
        { "\"_1\"", "ValueError", "invalid literal for int() with base 10: '_1'" },
        { "\"1_\"", "ValueError", "invalid literal for int() with base 10: '1_'" },
        { "\"007\"", "ok", "7" },
        { "\"\\uff11\\uff12\"", "ok", "12" },
        { "\"\\u2003-5\\u3000\"", "ok", "-5" },
        { "\"1\\u0000\"", "ValueError", "invalid literal for int() with base 10: '1\\x00'" },
        { "\"\\ud835\\udfce3\"", "ok", "3" },
        { "\"x\\u00e9\"", "ValueError", "invalid literal for int() with base 10: 'xé'" },
        { "null", "TypeError", "int() argument must be a string, a bytes-like object or a real number, not 'NoneType'" },
        { "[1]", "TypeError", "int() argument must be a string, a bytes-like object or a real number, not 'list'" },
        { "{\"a\":1}", "TypeError", "int() argument must be a string, a bytes-like object or a real number, not 'dict'" },
        { "2.9", "ok", "2" },
        { "-2.9", "ok", "-2" },
        { "1e400", "OverflowError", "cannot convert float infinity to integer" },
        { "NaN", "ValueError", "cannot convert float NaN to integer" },
        { "true", "ok", "1" },
        { "false", "ok", "0" },
        { "123456789012345678901234567890", "ok", "123456789012345678901234567890" },
    };

    public static TheoryData<string, string, string> FloatCases() => new()
    {
        { "\"0.01\"", "ok", "0.01" },
        { "\"soon\"", "ValueError", "could not convert string to float: 'soon'" },
        { "\" 1_0.5 \"", "ok", "10.5" },
        { "\"inf\"", "ok", "inf" },
        { "\"-Infinity\"", "ok", "-inf" },
        { "\"nAn\"", "ok", "nan" },
        { "\"1e5\"", "ok", "100000.0" },
        { "\"1_e5\"", "ValueError", "could not convert string to float: '1_e5'" },
        { "\"\\u0663.\\u0665\"", "ok", "3.5" },
        { "\".5\"", "ok", "0.5" },
        { "\"5.\"", "ok", "5.0" },
        { "\"1e\"", "ValueError", "could not convert string to float: '1e'" },
        { "\"0x1\"", "ValueError", "could not convert string to float: '0x1'" },
        { "\"1e999\"", "ok", "inf" },
        { "\"_1\"", "ValueError", "could not convert string to float: '_1'" },
        { "\"1_\"", "ValueError", "could not convert string to float: '1_'" },
        { "\"+-1\"", "ValueError", "could not convert string to float: '+-1'" },
        { "\"  \"", "ValueError", "could not convert string to float: '  '" },
        { "\"1_0e-4\"", "ok", "0.001" },
        { "true", "ok", "1.0" },
        { "[1]", "TypeError", "float() argument must be a string or a real number, not 'list'" },
        { "{}", "TypeError", "float() argument must be a string or a real number, not 'dict'" },
        { "1" + new string('0', 400), "OverflowError", "int too large to convert to float" },
        { "1e400", "ok", "inf" },
        { "3", "ok", "3.0" },
    };

    private static object? Decode(string json)
    {
        PythonJson.TryLoads(json, out var value).Should().BeTrue();
        return value;
    }

    private static void ShouldRaise(Action action, string kind, string message)
    {
        var raised = action.Should().Throw<Exception>().Which;
        var expectedType = kind switch
        {
            "ValueError" => typeof(PythonValueException),
            "TypeError" => typeof(PythonTypeException),
            "OverflowError" => typeof(OverflowException),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown Python exception"),
        };
        raised.Should().BeOfType(expectedType);
        raised.Message.Should().Be(message);
    }

    [Theory]
    [MemberData(nameof(IntCases))]
    public void IntMatchesPython(string json, string kind, string expected)
    {
        var value = Decode(json);
        if (string.Equals(kind, "ok", StringComparison.Ordinal))
        {
            PythonBuiltins.Int(value).ToString(CultureInfo.InvariantCulture).Should().Be(expected);
        }
        else
        {
            ShouldRaise(() => PythonBuiltins.Int(value), kind, expected);
        }
    }

    [Theory]
    [MemberData(nameof(FloatCases))]
    public void FloatMatchesPython(string json, string kind, string expected)
    {
        var value = Decode(json);
        if (string.Equals(kind, "ok", StringComparison.Ordinal))
        {
            PythonRepr.Float(PythonBuiltins.Float(value)).Should().Be(expected);
        }
        else
        {
            ShouldRaise(() => PythonBuiltins.Float(value), kind, expected);
        }
    }

    [Fact]
    public void AnIntLiteralPastTheDigitLimitRaisesAsPythonDoes()
    {
        var digits = new string('7', 4301);

        ShouldRaise(
            () => PythonBuiltins.Int(digits),
            "ValueError",
            "Exceeds the limit (4300 digits) for integer string conversion: value has 4301 digits; use sys.set_int_max_str_digits() to increase the limit");
    }

    [Fact]
    public void AnInvalidLiteralMessageKeepsTheFirst200CharactersOfTheRepr()
    {
        var text = new string('a', 300);

        ShouldRaise(() => PythonBuiltins.Int(text), "ValueError", "invalid literal for int() with base 10: '" + new string('a', 199));
    }

    [Fact]
    public void IterateYieldsCodePointsKeysAndItems()
    {
        PythonBuiltins.Iterate("a\U0001F600\ud800").Should().Equal("a", "\U0001F600", "\ud800");
        PythonBuiltins.Iterate(Decode("{\"x\": 1, \"y\": 2}")).Should().Equal("x", "y");
        PythonBuiltins.Iterate(Decode("[1, null]")).Should().Equal(1, null);
        ShouldRaise(() => PythonBuiltins.Iterate(Decode("2.5")), "TypeError", "'float' object is not iterable");
        ShouldRaise(() => PythonBuiltins.Iterate(null), "TypeError", "'NoneType' object is not iterable");
    }

    [Fact]
    public void GetOnAValueThatIsNotAMappingRaisesAttributeError()
    {
        PythonBuiltins.Get(Decode("{\"k\": 3}"), "k").Should().Be(3);
        PythonBuiltins.Get(Decode("{}"), "k").Should().BeNull();
        var get = () => PythonBuiltins.Get(Decode("[true]"), "k");
        get.Should().Throw<InvalidDataException>().Which.Message.Should().Be("'list' object has no attribute 'get'");
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData("\"\"", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("NaN", true)]
    [InlineData("\"0\"", true)]
    [InlineData("\"false\"", true)]
    [InlineData("[0]", true)]
    [InlineData("123456789012345678901234567890", true)]
    public void IsTruthyMatchesBool(string json, bool expected)
        => PythonBuiltins.IsTruthy(Decode(json)).Should().Be(expected);
}
