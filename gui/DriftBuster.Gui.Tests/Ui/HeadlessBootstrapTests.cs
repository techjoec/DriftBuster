using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace DriftBuster.Gui.Tests.Ui;

/// <summary>
/// Proves the headless host boots the real <see cref="App"/>: App.axaml styles (Fluent theme plus the
/// project style includes) are loaded, so templated controls can be shown without a font or theme shim.
/// </summary>
public sealed class HeadlessBootstrapTests
{
    [AvaloniaFact]
    public void Headless_app_loads_App_axaml_styles()
    {
        Application.Current.Should().BeOfType<App>();
        Application.Current!.Styles.Count.Should().BeGreaterThan(0);
    }

    [AvaloniaFact]
    public void Window_with_ToggleSwitch_shows_without_throwing()
    {
        var toggle = new ToggleSwitch { IsChecked = true, Content = "Enabled" };
        var window = new Window { Width = 320, Height = 200, Content = toggle };

        var act = () => window.Show();

        act.Should().NotThrow();
        toggle.IsVisible.Should().BeTrue();
        window.Close();
    }
}
