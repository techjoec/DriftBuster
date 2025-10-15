using System;
using System.IO;
using System.Linq;
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

    private async void OnSecretScannerSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RunProfilesViewModel viewModel)
        {
            return;
        }

        var window = new SecretScannerSettingsWindow
        {
            DataContext = new SecretScannerSettingsViewModel(viewModel.SecretScanner),
        };

        var owner = TopLevel.GetTopLevel(this) as Window;
        bool? result;
        if (owner is not null)
        {
            result = await window.ShowDialog<bool?>(owner).ConfigureAwait(true);
        }
        else
        {
            result = await window.ShowDialog<bool?>().ConfigureAwait(true);
        }

        if (result == true && window.DataContext is SecretScannerSettingsViewModel settingsViewModel)
        {
            viewModel.ApplySecretScanner(settingsViewModel.BuildResult());
        }
    }

    private async void OnPrepareOfflineCollector(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RunProfilesViewModel viewModel)
        {
            return;
        }

        var suggested = BuildOfflineCollectorName(viewModel.ProfileName);
        var packagePath = await PickSaveFileAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        await viewModel.PrepareOfflineCollectorAsync(packagePath).ConfigureAwait(true);
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

    private async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Zip archive")
                {
                    Patterns = new[] { "*.zip" },
                },
            },
        };

        var file = await storageProvider.SaveFilePickerAsync(options).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    private static string BuildOfflineCollectorName(string? profileName)
    {
        const string fallback = "offline-collector.zip";
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(profileName
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim('-', ' ');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "offline-collector";
        }

        if (!cleaned.EndsWith("-offline-collector", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = $"{cleaned}-offline-collector";
        }

        if (!cleaned.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            cleaned += ".zip";
        }

        return cleaned;
    }
}
