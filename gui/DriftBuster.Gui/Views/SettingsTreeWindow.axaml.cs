using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

/// <summary>A file's settings as a tree, each leaf with every server's value; branches that differ are highlighted.</summary>
[ExcludeFromCodeCoverage]
public partial class SettingsTreeWindow : Window
{
    public SettingsTreeWindow()
    {
        InitializeComponent();
    }

    public SettingsTreeWindow(string heading, IReadOnlyList<SettingsTreeNode> nodes)
        : this()
    {
        Title = heading;
        this.FindControl<TextBlock>("Heading")!.Text = heading;
        this.FindControl<TreeView>("Tree")!.ItemsSource = nodes;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
