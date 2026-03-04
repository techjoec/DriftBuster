using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Ui;

namespace DriftBuster.Gui.Tests.Services;

[Collection(HeadlessCollection.Name)]
public sealed class ApplicationThemeRuntimeTests
{
    [AvaloniaFact]
    public void GetAvailableThemes_returns_cached_options()
    {
        var runtime = new ApplicationThemeRuntime();

        var first = runtime.GetAvailableThemes();
        var second = runtime.GetAvailableThemes();

        first.Should().BeSameAs(second);
        first.Should().HaveCount(2);
        first.Select(option => option.Id).Should().Contain(new[] { "dark-plus", "light-plus" });
    }

    [Fact]
    public void GetDefaultTheme_throws_when_options_empty()
    {
        var runtime = new ApplicationThemeRuntime();
        var action = () => runtime.GetDefaultTheme(Array.Empty<ThemeOption>());
        action.Should().Throw<ArgumentException>();
    }

    [AvaloniaFact]
    public void GetDefaultTheme_prefers_requested_theme_variant()
    {
        var runtime = new ApplicationThemeRuntime();
        var app = Application.Current!;
        var originalVariant = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            var selected = runtime.GetDefaultTheme(runtime.GetAvailableThemes());
            selected.Id.Should().Be("light-plus");
        }
        finally
        {
            app.RequestedThemeVariant = originalVariant;
        }
    }

    [AvaloniaFact]
    public void GetDefaultTheme_falls_back_to_palette_id_resource()
    {
        var runtime = new ApplicationThemeRuntime();
        var app = Application.Current!;
        var originalVariant = app.RequestedThemeVariant;
        var hadDefaultPalette = app.Resources.TryGetValue("Theme.DefaultPaletteId", out var previousDefaultPalette);
        try
        {
            app.RequestedThemeVariant = new ThemeVariant("non-match", null);
            app.Resources["Theme.DefaultPaletteId"] = "light-plus";

            var selected = runtime.GetDefaultTheme(runtime.GetAvailableThemes());

            selected.Id.Should().Be("light-plus");
        }
        finally
        {
            app.RequestedThemeVariant = originalVariant;
            if (hadDefaultPalette)
            {
                app.Resources["Theme.DefaultPaletteId"] = previousDefaultPalette!;
            }
            else
            {
                app.Resources.Remove("Theme.DefaultPaletteId");
            }
        }
    }

    [AvaloniaFact]
    public void ApplyTheme_sets_variant_and_merges_palette_resources()
    {
        var runtime = new ApplicationThemeRuntime();
        var app = Application.Current!;
        var paletteKey = "Palette.TestRuntime";
        var activePaletteKey = "Theme.ActivePaletteId";
        var brushKey = "Brush.TestRuntime";
        var expectedBrush = new SolidColorBrush(Colors.Goldenrod);

        var originalVariant = app.RequestedThemeVariant;
        var hadPalette = app.Resources.TryGetValue(paletteKey, out var originalPalette);
        var hadBrush = app.Resources.TryGetValue(brushKey, out var originalBrush);
        var hadActivePalette = app.Resources.TryGetValue(activePaletteKey, out var originalActivePalette);
        try
        {
            app.Resources[paletteKey] = new ResourceDictionary
            {
                [brushKey] = expectedBrush,
            };

            runtime.ApplyTheme(new ThemeOption("test-runtime", "Test runtime", ThemeVariant.Dark, paletteKey));

            app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
            app.Resources[activePaletteKey].Should().Be("test-runtime");
            app.Resources[brushKey].Should().Be(expectedBrush);
        }
        finally
        {
            app.RequestedThemeVariant = originalVariant;
            if (hadPalette)
            {
                app.Resources[paletteKey] = originalPalette!;
            }
            else
            {
                app.Resources.Remove(paletteKey);
            }

            if (hadBrush)
            {
                app.Resources[brushKey] = originalBrush!;
            }
            else
            {
                app.Resources.Remove(brushKey);
            }

            if (hadActivePalette)
            {
                app.Resources[activePaletteKey] = originalActivePalette!;
            }
            else
            {
                app.Resources.Remove(activePaletteKey);
            }
        }
    }

    [AvaloniaFact]
    public void ApplyTheme_returns_when_palette_resource_is_missing()
    {
        var runtime = new ApplicationThemeRuntime();
        var app = Application.Current!;
        var activePaletteKey = "Theme.ActivePaletteId";
        var hadActivePalette = app.Resources.TryGetValue(activePaletteKey, out var previousActivePalette);
        var originalVariant = app.RequestedThemeVariant;
        try
        {
            runtime.ApplyTheme(new ThemeOption("missing", "Missing", ThemeVariant.Dark, "Palette.Does.Not.Exist"));
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
            if (hadActivePalette)
            {
                app.Resources[activePaletteKey].Should().Be(previousActivePalette);
            }
            else
            {
                app.Resources.ContainsKey(activePaletteKey).Should().BeFalse();
            }
        }
        finally
        {
            app.RequestedThemeVariant = originalVariant;
            if (hadActivePalette)
            {
                app.Resources[activePaletteKey] = previousActivePalette!;
            }
            else
            {
                app.Resources.Remove(activePaletteKey);
            }
        }
    }

    [Fact]
    public void ApplyTheme_throws_for_null_option()
    {
        var runtime = new ApplicationThemeRuntime();
        var action = () => runtime.ApplyTheme(null!);
        action.Should().Throw<ArgumentNullException>();
    }
}
