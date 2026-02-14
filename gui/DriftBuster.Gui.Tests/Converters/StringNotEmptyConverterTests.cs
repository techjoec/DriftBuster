using System.Globalization;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class StringNotEmptyConverterTests
{
    [Theory]
    [InlineData("hello", true)]
    [InlineData("  x  ", true)]
    [InlineData("value", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Convert_handles_string_variants(string input, bool expected)
    {
        StringNotEmptyConverter.Instance.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Fact]
    public void Convert_returns_false_for_null()
    {
        StringNotEmptyConverter.Instance.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void Convert_returns_false_for_non_string_types()
    {
        StringNotEmptyConverter.Instance.Convert(42, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
        StringNotEmptyConverter.Instance.Convert(true, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void ConvertBack_is_not_supported()
    {
        Action act = () => StringNotEmptyConverter.Instance.ConvertBack(true, typeof(string), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
