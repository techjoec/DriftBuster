using System.Diagnostics.CodeAnalysis;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

/// <summary>A setting's recorded history: its values over time, where else it is set, and where else its value appears.</summary>
[ExcludeFromCodeCoverage]
public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
    }

    public HistoryWindow(string heading, CompareHistory history, CompareHistoryKind kind)
        : this()
    {
        Title = heading;
        this.FindControl<TextBlock>("Heading")!.Text = heading;
        this.FindControl<TextBlock>("Empty")!.IsVisible = history.IsEmpty;
        this.FindControl<ListBox>("OverTime")!.ItemsSource = history.SettingOverTime;
        this.FindControl<ListBox>("Elsewhere")!.ItemsSource = history.SettingElsewhere;
        this.FindControl<ListBox>("ValueElsewhere")!.ItemsSource = history.ValueElsewhere;
        this.FindControl<TabItem>("OverTimeTab")!.IsVisible = kind != CompareHistoryKind.Value;
        this.FindControl<TabItem>("ElsewhereTab")!.IsVisible = kind != CompareHistoryKind.Value;
        this.FindControl<TabItem>("ValueTab")!.IsVisible = kind != CompareHistoryKind.Setting;
        this.FindControl<TabControl>("Tabs")!.SelectedIndex = kind == CompareHistoryKind.Value ? 2 : 0;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
