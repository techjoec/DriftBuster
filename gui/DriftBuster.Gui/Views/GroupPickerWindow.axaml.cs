using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views;

/// <summary>Picks an existing group or names a new one; closes with the name, or null when cancelled.</summary>
[ExcludeFromCodeCoverage]
public partial class GroupPickerWindow : Window
{
    public GroupPickerWindow()
    {
        InitializeComponent();
    }

    public GroupPickerWindow(string heading, IReadOnlyList<string> groups)
        : this()
    {
        this.FindControl<TextBlock>("Heading")!.Text = heading;
        var list = this.FindControl<ListBox>("Groups")!;
        list.ItemsSource = groups;
        if (groups.Count == 0)
        {
            this.FindControl<TextBox>("NewName")!.PlaceholderText = "Name the first group";
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnPick(object? sender, RoutedEventArgs e)
    {
        var typed = this.FindControl<TextBox>("NewName")!.Text?.Trim();
        var picked = typed is { Length: > 0 } ? typed : this.FindControl<ListBox>("Groups")!.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Close(picked);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
