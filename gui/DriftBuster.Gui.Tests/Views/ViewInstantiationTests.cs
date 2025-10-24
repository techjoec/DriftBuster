using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public void Theme_selector_updates_requested_variant()
    {
        HeadlessFixture.EnsureFonts();

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };

        var combo = window.FindControl<ComboBox>("ThemeSelector");
        combo.Should().NotBeNull();

        var viewModel = (MainWindowViewModel)window.DataContext!;
        var light = viewModel.ThemeOptions.First(option => option.Variant == ThemeVariant.Light);
        var dark = viewModel.ThemeOptions.First(option => option.Variant == ThemeVariant.Dark);

        combo!.SelectedItem = light;
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);

        combo.SelectedItem = dark;
        Application.Current.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
    }
}
