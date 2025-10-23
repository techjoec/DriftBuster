using System;
using System.Reflection;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

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
        Assert.IsAssignableFrom<IFontManagerImpl>(fontManager);

        var options = serviceMethod.Invoke(locator, new object[] { typeof(FontManagerOptions) });
        var managerOptions = Assert.IsType<FontManagerOptions>(options);

        Assert.Equal("Inter", managerOptions.DefaultFamilyName);
        var fallbacks = managerOptions.FontFallbacks;
        Assert.NotNull(fallbacks);
        Assert.Contains(fallbacks!, fallback =>
            string.Equals(fallback.FontFamily.Name, "Inter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureHeadless_allows_main_window_instantiation()
    {
        using var scope = Program.EnsureHeadless();

        var window = new DriftBuster.Gui.Views.MainWindow();
        Assert.NotNull(window);
    }
}
