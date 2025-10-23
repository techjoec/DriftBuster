using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

using DriftBuster.Gui.Headless;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class HeadlessBootstrapperSmokeTests
{
    [Fact]
    public void EnsureHeadless_registers_inter_font_manager()
    {
        using var scope = Program.EnsureHeadless();

        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as AvaloniaLocator;
        Assert.NotNull(locator);

        var serviceMethod = locator!.GetType().GetMethod("GetService", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Type) });
        Assert.NotNull(serviceMethod);

        var fontManager = serviceMethod!.Invoke(locator, new object[] { typeof(IFontManagerImpl) });
        var manager = Assert.IsAssignableFrom<IFontManagerImpl>(fontManager);

        var options = serviceMethod.Invoke(locator, new object[] { typeof(FontManagerOptions) });
        var managerOptions = Assert.IsType<FontManagerOptions>(options);

        Assert.Equal("Inter", managerOptions.DefaultFamilyName);
        var fallbacks = managerOptions.FontFallbacks;
        Assert.NotNull(fallbacks);
        var descriptors = fallbacks!
            .Select(fallback => fallback?.FontFamily)
            .Where(family => family is not null)
            .Cast<FontFamily>()
            .Select(family => new
            {
                Family = family,
                Name = family.Name,
                Descriptor = GetFontFamilyDescriptor(family),
            })
            .ToArray();

        Assert.Contains(descriptors, entry =>
            string.Equals(entry.Name, "Inter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Descriptor, "Inter", StringComparison.OrdinalIgnoreCase));
        Assert.True(descriptors.Any(entry =>
            string.Equals(entry.Name, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Descriptor, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase)),
            "fonts:SystemFonts alias should be part of the fallback chain.");

        var tryCreateGlyphTypeface = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface", new[]
        {
            typeof(string), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(IGlyphTypeface).MakeByRefType(),
        });

        var parameters = new object?[]
        {
            "fonts:SystemFonts",
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            null,
        };

        var success = tryCreateGlyphTypeface is not null && (bool)tryCreateGlyphTypeface.Invoke(manager, parameters)!;
        Assert.True(success);
        var aliasTypeface = Assert.IsAssignableFrom<IGlyphTypeface>(parameters[4]);
    }

    [Fact]
    public void EnsureHeadless_release_mode_exposes_inter_alias_through_system_fonts()
    {
        using var scope = Program.EnsureHeadless();

        var fontManager = FontManager.Current;
        Assert.NotNull(fontManager);

        var app = Assert.IsType<App>(Application.Current);
        const string resourceKey = "fonts:SystemFonts";
        Assert.True(app.Resources.TryGetValue(resourceKey, out var fontsResource));
        var resourceDictionary = Assert.IsAssignableFrom<IDictionary<string, FontFamily>>(fontsResource);

        Assert.True(resourceDictionary.ContainsKey(resourceKey));
        Assert.True(resourceDictionary.ContainsKey("Inter"));

        Assert.True(fontManager!.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts#Inter"), out var glyphTypeface),
            "Expected fonts:SystemFonts#Inter alias to resolve via the font manager in release mode.");
        Assert.NotNull(glyphTypeface);
        Assert.True(string.Equals("Inter", glyphTypeface!.FamilyName, StringComparison.OrdinalIgnoreCase),
            $"Expected glyph family to normalise to Inter but was '{glyphTypeface!.FamilyName}'.");
    }

    private static string? GetFontFamilyDescriptor(FontFamily family)
    {
        var sourceProperty = typeof(FontFamily).GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        var source = sourceProperty?.GetValue(family) as string;

        return string.IsNullOrWhiteSpace(source)
            ? family.Name
            : source;
    }

    [Fact]
    public void EnsureHeadless_allows_main_window_instantiation()
    {
        using var scope = Program.EnsureHeadless();

        var window = new DriftBuster.Gui.Views.MainWindow();
        Assert.NotNull(window);
    }
}
