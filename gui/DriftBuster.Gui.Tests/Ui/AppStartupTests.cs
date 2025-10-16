using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using DriftBuster.Gui;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class AppStartupTests
{
    [Fact]
    public void EnsureHeadless_Is_Idempotent()
    {
        using var scope = Program.EnsureHeadless();
        using var scope2 = Program.EnsureHeadless();

        Application.Current.Should().NotBeNull();
        Application.Current.Should().BeOfType<App>();
    }

    [Fact]
    public void FrameworkInitialization_assigns_main_window()
    {
        var app = new App();
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        app.ApplicationLifetime = lifetime;

        app.OnFrameworkInitializationCompleted();

        lifetime.MainWindow.Should().NotBeNull();
    }

}
