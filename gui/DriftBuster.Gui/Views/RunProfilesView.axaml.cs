using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

[ExcludeFromCodeCoverage]
public partial class RunProfilesView : UserControl
{
    internal Func<Task<string?>>? FilePickerOverride { get; set; }
    internal Func<Task<string?>>? FolderPickerOverride { get; set; }
    internal Func<Task<string?>>? SaveFilePickerOverride { get; set; }
    internal Func<SecretScannerSettingsViewModel, Task<SecretScannerOptions?>>? SecretScannerDialogOverride { get; set; }
    internal IStorageProvider? StorageProviderOverride { get; set; }

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

        if (SecretScannerDialogOverride is not null && window.DataContext is SecretScannerSettingsViewModel overrideViewModel)
        {
            var options = await SecretScannerDialogOverride(overrideViewModel).ConfigureAwait(true);
            if (options is not null)
            {
                viewModel.ApplySecretScanner(options);
            }

            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return;
        }

        var result = await window.ShowDialog<bool?>(owner).ConfigureAwait(true);

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
        if (FilePickerOverride is not null)
        {
            return await FilePickerOverride().ConfigureAwait(true);
        }

        var storageProvider = StorageProviderOverride ?? TopLevel.GetTopLevel(this)?.StorageProvider;
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
        if (FolderPickerOverride is not null)
        {
            return await FolderPickerOverride().ConfigureAwait(true);
        }

        var storageProvider = StorageProviderOverride ?? TopLevel.GetTopLevel(this)?.StorageProvider;
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
        if (SaveFilePickerOverride is not null)
        {
            return await SaveFilePickerOverride().ConfigureAwait(true);
        }

        var storageProvider = StorageProviderOverride ?? TopLevel.GetTopLevel(this)?.StorageProvider;
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
