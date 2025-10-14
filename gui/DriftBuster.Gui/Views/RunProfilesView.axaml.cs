using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

public partial class RunProfilesView : UserControl
{
    public RunProfilesView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not RunProfilesViewModel.SourceEntry entry)
        {
            return;
        }

        var file = await PickFileAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(file))
        {
            entry.Path = file;
        }
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not RunProfilesViewModel.SourceEntry entry)
        {
            return;
        }

        var folder = await PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(folder))
        {
            entry.Path = folder;
        }
    }

    private async Task<string?> PickFileAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return null;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return null;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
