using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class SecretScannerSettingsWindowTests
{
    [AvaloniaFact]
    public void Window_loads_with_expected_title()
    {
        var options = new SecretScannerOptions
        {
            IgnoreRules = new[] { "rule-one" },
            IgnorePatterns = new[] { "pattern" },
        };

        var window = new SecretScannerSettingsWindow
        {
            DataContext = new SecretScannerSettingsViewModel(options),
        };

        window.Title.Should().Be("Secret scanner settings");
    }
}
