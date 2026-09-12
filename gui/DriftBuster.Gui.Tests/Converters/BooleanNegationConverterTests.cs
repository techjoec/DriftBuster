using System.Globalization;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class BooleanNegationConverterTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData("unexpected", true)]
    public void Convert_and_ConvertBack_negate_booleans_and_default_to_true(object? input, bool expected)
    {
        var converter = BooleanNegationConverter.Instance;

        converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(expected);
        converter.ConvertBack(input, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(expected);
    }
}
