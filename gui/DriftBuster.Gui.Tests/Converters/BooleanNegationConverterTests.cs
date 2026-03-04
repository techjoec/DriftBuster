using System.Globalization;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class BooleanNegationConverterTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Convert_negates_boolean_values(bool input, bool expected)
    {
        var converter = BooleanNegationConverter.Instance;

        converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    public void Convert_negates_nullable_boolean_values(bool? input, bool expected)
    {
        var converter = BooleanNegationConverter.Instance;

        converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public void Convert_defaults_to_true_for_non_boolean_values()
    {
        var converter = BooleanNegationConverter.Instance;

        converter.Convert("unexpected", typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(true);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ConvertBack_negates_boolean_values(bool input, bool expected)
    {
        var converter = BooleanNegationConverter.Instance;

        converter.ConvertBack(input, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public void ConvertBack_defaults_to_true_for_non_boolean_values()
    {
        var converter = BooleanNegationConverter.Instance;

        converter.ConvertBack("unexpected", typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(true);
    }
}
