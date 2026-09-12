using Avalonia;
using Avalonia.Headless;
using DriftBuster.Gui;
using DriftBuster.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DriftBuster.Gui.Tests;

/// <summary>
/// Headless bootstrap for the xUnit suite: the real desktop <see cref="AppBuilder"/> (App.axaml with the
/// Fluent theme and the embedded Inter font) topped with the headless platform so every test sees the same
/// styles and resources the shipped app loads.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => Program.BuildAvaloniaApp()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
