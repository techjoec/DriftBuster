using Avalonia;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Views;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class MainWindowUiTests
{
    [AvaloniaFact]
    public void ShouldAdjustSpacingTokensAcrossBreakpoints()
    {
        var window = new MainWindow();

        ResponsiveLayoutService.Apply(window, 1200, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(12, 8, 12, 8));
        window.Resources["Toast.Width"].Should().Be(320d);

        ResponsiveLayoutService.Apply(window, 1400, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(16, 8, 16, 8));
        window.Resources["Toast.Width"].Should().Be(360d);

        ResponsiveLayoutService.Apply(window, 1700, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(20, 10, 20, 10));
        window.Resources["Toast.Width"].Should().Be(400d);

        ResponsiveLayoutService.Apply(window, 2100, ResponsiveSpacingProfiles.MainWindow);
        window.Resources["Layout.HeaderPadding"].Should().Be(new Thickness(24, 12, 24, 12));
        window.Resources["Toast.Width"].Should().Be(440d);
    }
}
