using System;
using System.Globalization;

using Avalonia.Headless.XUnit;
using Avalonia.Media;

using DriftBuster.Gui.Converters;
using DriftBuster.Gui.Tests.Ui;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.Converters;

[Collection(HeadlessCollection.Name)]
public sealed class RootStatusToBrushConverterTests
{
    [AvaloniaTheory]
    [InlineData(RootValidationState.Valid, "#144f2a")]
    [InlineData(RootValidationState.Invalid, "#64241f")]
    [InlineData(RootValidationState.Pending, "#1e293b")]
    public void Convert_maps_state_to_expected_color(RootValidationState state, string hex)
    {
        var converter = RootStatusToBrushConverter.Instance;

        var result = converter.Convert(state, typeof(IBrush), null, CultureInfo.InvariantCulture);

        var brush = result.Should().BeOfType<SolidColorBrush>().Subject;
        brush.Color.Should().Be(Color.Parse(hex));
    }

    [AvaloniaFact]
    public void Convert_defaults_to_pending_for_non_enum_values()
    {
        var converter = RootStatusToBrushConverter.Instance;

        var result = converter.Convert("not-a-state", typeof(IBrush), null, CultureInfo.InvariantCulture);

        var brush = result.Should().BeOfType<SolidColorBrush>().Subject;
        brush.Color.Should().Be(Color.Parse("#1e293b"));
    }

    [AvaloniaFact]
    public void ConvertBack_is_not_supported()
    {
        var converter = RootStatusToBrushConverter.Instance;

        Action act = () => converter.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }
}
