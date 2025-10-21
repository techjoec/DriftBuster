using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using DriftBuster.Gui.Converters;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class BoolToBrushConverterTests
{
    [AvaloniaFact]
    public async Task Uses_parameter_colours_when_supplied()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var brush = (SolidColorBrush)BoolToBrushConverter.Instance.Convert(true, typeof(SolidColorBrush), "#FF0000;#0000FF", CultureInfo.InvariantCulture)!;
            brush.Color.Should().Be(Color.Parse("#FF0000"));

            var falseBrush = (SolidColorBrush)BoolToBrushConverter.Instance.Convert(false, typeof(SolidColorBrush), "#FF0000;#0000FF", CultureInfo.InvariantCulture)!;
            falseBrush.Color.Should().Be(Color.Parse("#0000FF"));
        });
    }

    [AvaloniaFact]
    public async Task Falls_back_to_default_palette()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var trueBrush = (SolidColorBrush)BoolToBrushConverter.Instance.Convert(true, typeof(SolidColorBrush), string.Empty, CultureInfo.InvariantCulture)!;
            var falseBrush = (SolidColorBrush)BoolToBrushConverter.Instance.Convert(false, typeof(SolidColorBrush), string.Empty, CultureInfo.InvariantCulture)!;

            trueBrush.Color.Should().NotBe(falseBrush.Color);
        });
    }
}
