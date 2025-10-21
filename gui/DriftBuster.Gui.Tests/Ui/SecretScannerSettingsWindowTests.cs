using Avalonia.Controls;
using Avalonia.Interactivity;

using DriftBuster.Backend.Models;
using Avalonia.Headless.XUnit;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class SecretScannerSettingsWindowTests
{
    [AvaloniaFact]
    public void Should_Bind_SecretScanner_ViewModel()
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

        window.DataContext.Should().BeOfType<SecretScannerSettingsViewModel>();
        window.Title.Should().Be("Secret scanner settings");
    }

    [AvaloniaFact]
    public void Confirm_closes_window()
    {
        var window = new SecretScannerSettingsWindow();
        Invoke(window, "OnConfirm", new Button(), new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public void Cancel_closes_window()
    {
        var window = new SecretScannerSettingsWindow();

        Invoke(window, "OnCancel", new Button(), new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(target, args);
    }
}
