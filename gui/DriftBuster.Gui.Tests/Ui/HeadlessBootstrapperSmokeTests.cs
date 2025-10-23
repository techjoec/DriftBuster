using System;
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
        Assert.Contains(fallbacks!, fallback =>
            string.Equals(fallback.FontFamily.Name, "Inter", StringComparison.OrdinalIgnoreCase));

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
    public void EnsureHeadless_allows_main_window_instantiation()
    {
        using var scope = Program.EnsureHeadless();

        var window = new DriftBuster.Gui.Views.MainWindow();
        Assert.NotNull(window);
    }
}
