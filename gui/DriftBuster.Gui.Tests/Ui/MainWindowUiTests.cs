using Avalonia;
using Avalonia.Headless.XUnit;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowUiTests
{
    [AvaloniaFact]
    public void Should_Create_MainWindow_With_ViewModel()
    {
        HeadlessFixture.EnsureFonts();

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };

        window.DataContext.Should().BeOfType<MainWindowViewModel>();
    }

    [AvaloniaFact]
    public void ShouldAdjustSpacingTokensAcrossBreakpoints()
    {
        HeadlessFixture.EnsureFonts();

        var window = new MainWindow();

        ResponsiveLayoutService.Apply(window, 1200, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(20, 16, 20, 16));
        window.Resources["Toast.Width"].Should().Be(320d);

        ResponsiveLayoutService.Apply(window, 1400, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(24, 20, 24, 20));
        window.Resources["Toast.Width"].Should().Be(360d);

        ResponsiveLayoutService.Apply(window, 1700, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(28, 22, 28, 22));
        window.Resources["Toast.Width"].Should().Be(400d);

        ResponsiveLayoutService.Apply(window, 2100, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(32, 24, 32, 24));
        window.Resources["Toast.Width"].Should().Be(440d);
    }
}
