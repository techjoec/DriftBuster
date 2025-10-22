using System;
using System.Collections.Concurrent;
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

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _scope?.Dispose();
        _scope = null;
        return Task.CompletedTask;
    }
}
