using System;
using System.Globalization;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class StringNotEmptyConverterTests
{
    [Fact]
    public void Convert_returns_true_for_non_empty_string()
    {
        StringNotEmptyConverter.Instance.Convert("value", typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(true);
        StringNotEmptyConverter.Instance.Convert("   ", typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
        StringNotEmptyConverter.Instance.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void ConvertBack_is_not_supported()
    {
        Action act = () => StringNotEmptyConverter.Instance.ConvertBack(true, typeof(string), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
