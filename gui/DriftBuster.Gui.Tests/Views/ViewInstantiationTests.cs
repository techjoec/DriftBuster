using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using DriftBuster.Gui.Tests.Ui;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Views;

[Collection(HeadlessCollection.Name)]
public sealed class ViewInstantiationTests
{
    [AvaloniaFact]
    public void ConfigDrilldown_and_results_views_load()
    {
        var drilldownView = new ConfigDrilldownView();
        drilldownView.Content.Should().NotBeNull();

        var catalogView = new ResultsCatalogView();
        catalogView.Content.Should().NotBeNull();
    }

    [AvaloniaFact]
    public void Theme_toggle_updates_requested_variant()
    {
        HeadlessFixture.EnsureFonts();

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };

        var toggle = new ToggleSwitch { IsChecked = true };
        var method = typeof(MainWindow).GetMethod("OnThemeToggled", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(window, new object?[] { toggle, new RoutedEventArgs() });
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);

        toggle.IsChecked = false;
        method.Invoke(window, new object?[] { toggle, new RoutedEventArgs() });
        Application.Current.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
    }
}
