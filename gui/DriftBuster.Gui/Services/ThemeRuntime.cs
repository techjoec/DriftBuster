using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DriftBuster.Gui.Services;

public interface IThemeRuntime
{
    IReadOnlyList<ThemeOption> GetAvailableThemes();

    ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options);

    void ApplyTheme(ThemeOption option);
}

public sealed record ThemeOption(string Id, string DisplayName, ThemeVariant Variant, string PaletteResourceKey);

public sealed class ApplicationThemeRuntime : IThemeRuntime
{
    public static ApplicationThemeRuntime Instance { get; } = new();

    private IReadOnlyList<ThemeOption>? _cachedOptions;

    public IReadOnlyList<ThemeOption> GetAvailableThemes()
    {
        if (_cachedOptions is not null)
        {
            return _cachedOptions;
        }

        _cachedOptions = new List<ThemeOption>
        {
            new("dark-plus", "Dark+", ThemeVariant.Dark, "Palette.DarkPlus"),
            new("light-plus", "Light+", ThemeVariant.Light, "Palette.LightPlus"),
        };

        return _cachedOptions;
    }

    public ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options)
    {
        if (options.Count == 0)
        {
            throw new ArgumentException("At least one theme option is required.", nameof(options));
        }

        var app = Application.Current;
        if (app is null)
        {
            return options[0];
        }

        var preferredVariant = app.RequestedThemeVariant ?? app.ActualThemeVariant;
        if (preferredVariant is not null)
        {
            var match = options.FirstOrDefault(o => o.Variant == preferredVariant);
            if (match is not null)
            {
                return match;
            }
        }

        if (app.TryFindResource("Theme.DefaultPaletteId", out var defaultIdObj) && defaultIdObj is string defaultId)
        {
            var match = options.FirstOrDefault(o => string.Equals(o.Id, defaultId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return options[0];
    }

    public void ApplyTheme(ThemeOption option)
    {
        if (option is null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.RequestedThemeVariant = option.Variant;

        if (!app.TryFindResource(option.PaletteResourceKey, out var paletteObj) || paletteObj is not IResourceDictionary palette)
        {
            return;
        }

        foreach (var key in palette.Keys)
        {
            app.Resources[key] = palette[key]!;
        }

        app.Resources["Theme.ActivePaletteId"] = option.Id;
    }
}
