using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

/// <summary>The Manage choices window; file pickers and confirmations live here, the rest in the view model.</summary>
[ExcludeFromCodeCoverage]
public partial class CurationManagerWindow : Window
{
    public CurationManagerWindow()
    {
        InitializeComponent();
    }

    public CurationManagerWindow(CurationManagerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private CurationManagerViewModel ViewModel => (CurationManagerViewModel)DataContext!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnClose(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDirty && await Confirm("Save your changes before closing?", "Save", "Discard").ConfigureAwait(true))
        {
            ViewModel.Save();
        }

        Close();
    }

    private async void OnClearHistory(object? sender, RoutedEventArgs e)
    {
        if (await Confirm("Remove every recorded scan from the history? This cannot be undone.", "Clear history", "Keep").ConfigureAwait(true))
        {
            ViewModel.ClearHistory();
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export choices",
            SuggestedFileName = "driftbuster-curation.json",
            DefaultExtension = "json",
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            Run(() => ViewModel.Export(path));
        }
    }

    private async void OnImportMerge(object? sender, RoutedEventArgs e) => await Import(replace: false).ConfigureAwait(true);

    private async void OnImportReplace(object? sender, RoutedEventArgs e) => await Import(replace: true).ConfigureAwait(true);

    private async Task Import(bool replace)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import choices", AllowMultiple = false }).ConfigureAwait(true);
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (replace && !await Confirm("Replace all your groups, rules and choices with the imported ones?", "Replace", "Cancel").ConfigureAwait(true))
        {
            return;
        }

        Run(() => ViewModel.Import(path, replace));
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async Task<bool> Confirm(string question, string yes, string no)
    {
        var dialog = new Window
        {
            Title = Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yesButton = new Button { Content = yes, Classes = { "primary" } };
        var noButton = new Button { Content = no, Classes = { "outline" } };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { noButton, yesButton } },
            },
        };
        return await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
    }
}
