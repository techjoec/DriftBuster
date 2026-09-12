using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

using DriftBuster.Gui;
using DriftBuster.Gui.Headless;

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

        Assert.IsType<App>(Application.Current);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _scope?.Dispose();
        _scope = null;
        return Task.CompletedTask;
    }

    public static void EnsureFonts()
    {
        if (Application.Current is App app)
        {
            App.EnsureFontResources(app);
        }

        var fontManager = FontManager.Current;
        HeadlessFontBootstrapper.EnsureSystemFonts(fontManager);
        HeadlessFontBootstrapper.EnsureSystemFontsDictionary(fontManager);
    }
}
