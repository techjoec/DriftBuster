using System;
using System.Collections.Generic;
using System.Globalization;
using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class CountToBooleanConverterTests
{
    private static readonly CountToBooleanConverter Converter = CountToBooleanConverter.Instance;

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Convert_returns_expected_value_for_int_counts(int value, bool expected)
    {
        var result = Converter.Convert(value, typeof(bool), parameter: null, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Fact]
    public void Convert_returns_true_for_non_empty_enumerable()
    {
        var result = Converter.Convert(new[] { "one" }, typeof(bool), parameter: null, CultureInfo.InvariantCulture);
        result.Should().Be(true);
    }

    [Fact]
    public void Convert_returns_false_for_empty_enumerable()
    {
        var result = Converter.Convert(Array.Empty<string>(), typeof(bool), parameter: null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_returns_false_for_non_supported_values()
    {
        var result = Converter.Convert(new object(), typeof(bool), parameter: null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }

    [Fact]
    public void ConvertBack_throws_not_supported()
    {
        var action = () => Converter.ConvertBack(true, typeof(int), parameter: null, CultureInfo.InvariantCulture);
        action.Should().Throw<NotSupportedException>();
    }
}
