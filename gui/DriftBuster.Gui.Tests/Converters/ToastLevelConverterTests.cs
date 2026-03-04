using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AwesomeAssertions;
using DriftBuster.Gui.Converters;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Ui;

namespace DriftBuster.Gui.Tests.Converters;

[Collection(HeadlessCollection.Name)]
public sealed class ToastLevelConverterTests
{
    [AvaloniaFact]
    public async Task Brush_converter_reads_theme_variant_resources()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            var resourceKey = "Brush.Toast.Success";
            var expected = new SolidColorBrush(Colors.LimeGreen);

            var originalVariant = app.RequestedThemeVariant;
            var themeDictionaries = app.Resources.ThemeDictionaries;
            var hadExistingTheme = themeDictionaries.TryGetValue(ThemeVariant.Dark, out var originalThemeDictionary);

            try
            {
                var themedResources = new ResourceDictionary
                {
                    [resourceKey] = expected,
                };

                themeDictionaries[ThemeVariant.Dark] = themedResources;
                app.RequestedThemeVariant = ThemeVariant.Dark;

                var brush = ToastLevelToBrushConverter.Instance
                    .Convert(ToastLevel.Success, typeof(IBrush), null, CultureInfo.InvariantCulture)
                    .Should().BeAssignableTo<IBrush>().Subject;

                brush.Should().BeSameAs(expected);
            }
            finally
            {
                app.RequestedThemeVariant = originalVariant;
                if (hadExistingTheme && originalThemeDictionary is not null)
                {
                    themeDictionaries[ThemeVariant.Dark] = originalThemeDictionary;
                }
                else
                {
                    themeDictionaries.Remove(ThemeVariant.Dark);
                }
            }
        });
    }

    [AvaloniaFact]
    public async Task Icon_converter_returns_empty_string_when_resource_missing()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var value = ToastLevelToIconConverter.Instance
                .Convert(ToastLevel.Warning, typeof(string), null, CultureInfo.InvariantCulture);

            value.Should().BeOfType<string>().Which.Should().BeEmpty();
        });
    }

    [AvaloniaFact]
    public async Task Icon_converter_reads_string_resources()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            const string resourceKey = "Toast.Icon.Error";
            const string glyph = "\u26A0";

            try
            {
                app.Resources[resourceKey] = glyph;

                var value = ToastLevelToIconConverter.Instance
                    .Convert(ToastLevel.Error, typeof(string), null, CultureInfo.InvariantCulture)
                    .Should().BeOfType<string>().Subject;

                value.Should().Be(glyph);
            }
            finally
            {
                app.Resources.Remove(resourceKey);
            }
        });
    }

    [Fact]
    public void Brush_converter_returns_gray_for_non_toast_value()
    {
        var value = ToastLevelToBrushConverter.Instance.Convert("not-level", typeof(IBrush), null, CultureInfo.InvariantCulture);
        value.Should().BeSameAs(Brushes.Gray);
    }

    [AvaloniaFact]
    public async Task Brush_converter_returns_gray_when_theme_resource_missing()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            const string resourceKey = "Brush.Toast.Warning";
            var hadResource = app.Resources.TryGetValue(resourceKey, out var previousValue);

            try
            {
                if (hadResource)
                {
                    app.Resources.Remove(resourceKey);
                }

                var brush = ToastLevelToBrushConverter.Instance.Convert(
                    ToastLevel.Warning,
                    typeof(IBrush),
                    null,
                    CultureInfo.InvariantCulture);

                brush.Should().BeSameAs(Brushes.Gray);
            }
            finally
            {
                if (hadResource)
                {
                    app.Resources[resourceKey] = previousValue!;
                }
            }
        });
    }

    [Fact]
    public void Brush_converter_convert_back_throws()
    {
        var action = () => ToastLevelToBrushConverter.Instance.ConvertBack(
            Brushes.Gray,
            typeof(ToastLevel),
            null,
            CultureInfo.InvariantCulture);

        action.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Icon_converter_returns_empty_for_non_toast_value()
    {
        var value = ToastLevelToIconConverter.Instance.Convert("unexpected", typeof(string), null, CultureInfo.InvariantCulture);
        value.Should().Be(string.Empty);
    }

    [Fact]
    public void Icon_converter_convert_back_throws()
    {
        var action = () => ToastLevelToIconConverter.Instance.ConvertBack(
            "x",
            typeof(ToastLevel),
            null,
            CultureInfo.InvariantCulture);

        action.Should().Throw<NotSupportedException>();
    }

    [AvaloniaFact]
    public async Task Icon_converter_reads_success_and_info_resources()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            const string successKey = "Toast.Icon.Success";
            const string infoKey = "Toast.Icon.Info";
            const string successGlyph = "+";
            const string infoGlyph = "i";

            try
            {
                app.Resources[successKey] = successGlyph;
                app.Resources[infoKey] = infoGlyph;

                var success = ToastLevelToIconConverter.Instance
                    .Convert(ToastLevel.Success, typeof(string), null, CultureInfo.InvariantCulture)
                    .Should().BeOfType<string>().Subject;
                var info = ToastLevelToIconConverter.Instance
                    .Convert(ToastLevel.Info, typeof(string), null, CultureInfo.InvariantCulture)
                    .Should().BeOfType<string>().Subject;

                success.Should().Be(successGlyph);
                info.Should().Be(infoGlyph);
            }
            finally
            {
                app.Resources.Remove(successKey);
                app.Resources.Remove(infoKey);
            }
        });
    }

    [AvaloniaFact]
    public async Task Brush_converter_reads_error_and_info_resources()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var app = Application.Current!;
            const string errorKey = "Brush.Toast.Error";
            const string infoKey = "Brush.Toast.Info";
            var errorBrush = new SolidColorBrush(Colors.OrangeRed);
            var infoBrush = new SolidColorBrush(Colors.CornflowerBlue);

            try
            {
                app.Resources[errorKey] = errorBrush;
                app.Resources[infoKey] = infoBrush;

                var error = ToastLevelToBrushConverter.Instance
                    .Convert(ToastLevel.Error, typeof(IBrush), null, CultureInfo.InvariantCulture)
                    .Should().BeAssignableTo<IBrush>().Subject;
                var info = ToastLevelToBrushConverter.Instance
                    .Convert(ToastLevel.Info, typeof(IBrush), null, CultureInfo.InvariantCulture)
                    .Should().BeAssignableTo<IBrush>().Subject;

                error.Should().BeSameAs(errorBrush);
                info.Should().BeSameAs(infoBrush);
            }
            finally
            {
                app.Resources.Remove(errorKey);
                app.Resources.Remove(infoKey);
            }
        });
    }
}
