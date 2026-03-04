using System.Collections.Generic;
using System.Globalization;

using Avalonia.Data;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class DiffJsonViewModeMatchConverterTests
{
    [Fact]
    public void Convert_returns_false_when_values_are_missing()
    {
        var converter = DiffJsonViewModeMatchConverter.Instance;

        converter.Convert(new List<object?>(), typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
        converter.Convert(new List<object?> { "left" }, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
    }

    [Fact]
    public void Convert_returns_false_when_any_side_is_null()
    {
        var converter = DiffJsonViewModeMatchConverter.Instance;

        converter.Convert(new List<object?> { null, "raw" }, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
        converter.Convert(new List<object?> { "raw", null }, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
    }

    [Fact]
    public void Convert_compares_values_for_equality()
    {
        var converter = DiffJsonViewModeMatchConverter.Instance;

        converter.Convert(new List<object?> { "raw", "raw" }, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(true);
        converter.Convert(new List<object?> { "raw", "sanitized" }, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
    }

    [Fact]
    public void ConvertBack_returns_do_nothing_marker()
    {
        var converter = DiffJsonViewModeMatchConverter.Instance;

        var result = converter.ConvertBack(new List<object?>(), typeof(object), null, CultureInfo.InvariantCulture);

        result.Should().Be(BindingOperations.DoNothing);
    }
}
