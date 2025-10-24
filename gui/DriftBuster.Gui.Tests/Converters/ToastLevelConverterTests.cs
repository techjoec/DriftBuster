using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using DriftBuster.Gui.Converters;
using DriftBuster.Gui.Services;

using FluentAssertions;

namespace DriftBuster.Gui.Tests.Converters;

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
}
