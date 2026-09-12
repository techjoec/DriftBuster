using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AwesomeAssertions;
using DriftBuster.Gui.Converters;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Ui;

namespace DriftBuster.Gui.Tests.Converters;

[Collection(HeadlessCollection.Name)]
public sealed class ToastLevelConverterTests
{
    [Fact]
    public void Brush_converter_returns_gray_for_non_toast_value()
    {
        var value = ToastLevelToBrushConverter.Instance.Convert("not-level", typeof(IBrush), null, CultureInfo.InvariantCulture);
        value.Should().BeSameAs(Brushes.Gray);
    }

    [Fact]
    public void Icon_converter_returns_empty_for_non_toast_value()
    {
        var value = ToastLevelToIconConverter.Instance.Convert("unexpected", typeof(string), null, CultureInfo.InvariantCulture);
        value.Should().Be(string.Empty);
    }

    [Fact]
    public void Converters_do_not_support_convert_back()
    {
        var brush = () => ToastLevelToBrushConverter.Instance.ConvertBack(Brushes.Gray, typeof(ToastLevel), null, CultureInfo.InvariantCulture);
        var icon = () => ToastLevelToIconConverter.Instance.ConvertBack("x", typeof(ToastLevel), null, CultureInfo.InvariantCulture);

        brush.Should().Throw<NotSupportedException>();
        icon.Should().Throw<NotSupportedException>();
    }

    [AvaloniaFact]
    public async Task Converters_resolve_level_keyed_application_resources()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            const string brushKey = "Brush.Toast.Error";
            const string iconKey = "Toast.Icon.Error";
            var errorBrush = new SolidColorBrush(Colors.OrangeRed);
            const string glyph = "⚠";

            try
            {
                app.Resources[brushKey] = errorBrush;
                app.Resources[iconKey] = glyph;

                ToastLevelToBrushConverter.Instance
                    .Convert(ToastLevel.Error, typeof(IBrush), null, CultureInfo.InvariantCulture)
                    .Should().BeSameAs(errorBrush);
                ToastLevelToIconConverter.Instance
                    .Convert(ToastLevel.Error, typeof(string), null, CultureInfo.InvariantCulture)
                    .Should().Be(glyph);
            }
            finally
            {
                app.Resources.Remove(brushKey);
                app.Resources.Remove(iconKey);
            }
        });
    }
}
