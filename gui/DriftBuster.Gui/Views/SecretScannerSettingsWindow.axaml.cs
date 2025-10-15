using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views;

public partial class SecretScannerSettingsWindow : Window
{
    public SecretScannerSettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
