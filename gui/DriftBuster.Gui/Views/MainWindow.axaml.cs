using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace DriftBuster.Gui.Views
{
    public partial class MainWindow : Avalonia.Controls.Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnThemeToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.ToggleSwitch ts)
            {
                // On = Light theme, Off = Dark theme (default)
                var variant = (ts.IsChecked ?? false) ? ThemeVariant.Light : ThemeVariant.Dark;
                Application.Current!.RequestedThemeVariant = variant;
            }
        }
    }
}
