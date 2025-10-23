using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class HeadlessFixture : IAsyncLifetime
{
    private IDisposable? _scope;

    public Task InitializeAsync()
    {
        _scope = Program.EnsureHeadless(builder => builder.UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = true,
        }));

        var app = Assert.IsType<App>(Application.Current);
        const string resourceKey = "fonts:SystemFonts";

        Assert.True(app.Resources.TryGetValue(resourceKey, out var fonts),
            $"Expected '{resourceKey}' resource to be populated before window creation.");

        var dictionary = Assert.IsType<ConcurrentDictionary<string, FontFamily>>(fonts);
        Assert.True(dictionary.Count > 0, "Expected at least one font family to be available for headless runs.");
        Assert.True(dictionary.ContainsKey(resourceKey), "fonts:SystemFonts alias should exist for Avalonia font manager access.");
        Assert.Contains(dictionary, pair => pair.Key.Equals("Inter", StringComparison.OrdinalIgnoreCase));

        var fontManager = FontManager.Current;
        Assert.NotNull(fontManager);
        var defaultFamilyName = fontManager.DefaultFontFamily.Name;
        Assert.Contains(new[] { "Inter", "fonts:SystemFonts" }, value =>
            value.Equals(defaultFamilyName, StringComparison.OrdinalIgnoreCase));

        var locatorProperty = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static);
        var locator = Assert.IsAssignableFrom<AvaloniaLocator>(locatorProperty?.GetValue(null));
        var getService = locator.GetType().GetMethod("GetService", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Type) });
        Assert.NotNull(getService);

        var options = getService!.Invoke(locator, new object[] { typeof(FontManagerOptions) });
        var managerOptions = Assert.IsType<FontManagerOptions>(options);
        Assert.Equal("Inter", managerOptions.DefaultFamilyName);
        Assert.True(string.Equals(managerOptions.DefaultFamilyName, defaultFamilyName, StringComparison.OrdinalIgnoreCase),
            "Default font family should align between FontManager and FontManagerOptions regardless of configuration.");

        var fallbackFamilies = managerOptions.FontFallbacks?
            .Select(fallback => fallback?.FontFamily)
            .Where(family => family is not null)
            .Cast<FontFamily>()
            .ToArray() ?? Array.Empty<FontFamily>();

        Assert.Contains(fallbackFamilies, family =>
            string.Equals(family.Name, "Inter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fallbackFamilies, family =>
            string.Equals(family.Name, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase));

        var sourceProperty = typeof(FontFamily).GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        if (sourceProperty is not null)
        {
            var fallbackSources = fallbackFamilies
                .Select(family => sourceProperty.GetValue(family) as string)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source!)
                .ToArray();

            Assert.Contains("fonts:SystemFonts", fallbackSources, StringComparer.OrdinalIgnoreCase);
        }

        Assert.True(fontManager.TryGetGlyphTypeface(new Typeface("Inter"), out var interGlyph),
            "Expected Inter glyph typeface to resolve in headless runs.");
        Assert.NotNull(interGlyph);
        Assert.Equal("Inter", interGlyph.FamilyName, StringComparer.OrdinalIgnoreCase);

        Assert.True(fontManager.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts"), out var aliasGlyph),
            "Expected fonts:SystemFonts alias to resolve through the headless font manager proxy.");
        Assert.NotNull(aliasGlyph);
        Assert.Contains(new[] { "Inter", "fonts:SystemFonts" }, value =>
            value.Equals(aliasGlyph.FamilyName, StringComparison.OrdinalIgnoreCase));

        Assert.True(fontManager.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts#Inter"), out var interAliasGlyph),
            "Expected fonts:SystemFonts#Inter alias to resolve for parity between Release and Debug.");
        Assert.NotNull(interAliasGlyph);
        Assert.True(string.Equals("Inter", interAliasGlyph.FamilyName, StringComparison.OrdinalIgnoreCase),
            "fonts:SystemFonts#Inter should normalise to the Inter glyph family.");

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _scope?.Dispose();
        _scope = null;
        return Task.CompletedTask;
    }
}
