using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    [ExcludeFromCodeCoverage]
    public partial class DiffView : UserControl
    {
        internal Func<Task<string?>>? FilePickerOverride { get; set; }
        internal Func<string, Task>? ClipboardSetTextOverride { get; set; }
        internal IStorageProvider? StorageProviderOverride { get; set; }

        public DiffView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnBrowseFile(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not DiffViewModel vm)
            {
                return;
            }

            if (sender is not Button button || button.Tag is not DiffViewModel.DiffInput input)
            {
                return;
            }

            var file = await PickSingleFileAsync();
            if (file is not null)
            {
                input.Path = file;
            }
        }

        private async Task<string?> PickSingleFileAsync()
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
            });

            if (files.Count == 0)
            {
                return null;
            }

            return files[0].TryGetLocalPath();
        }

        private async void OnCopyActiveJson(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not DiffViewModel vm)
            {
                return;
            }

            if (!vm.TryGetCopyPayload(out var payload))
            {
                return;
            }

            if (ClipboardSetTextOverride is not null)
            {
                await ClipboardSetTextOverride(payload).ConfigureAwait(true);
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(payload).ConfigureAwait(true);
        }

        private async void OnCopyDiff(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (button.Tag is not string diffText || string.IsNullOrWhiteSpace(diffText))
            {
                return;
            }

            if (ClipboardSetTextOverride is not null)
            {
                await ClipboardSetTextOverride(diffText).ConfigureAwait(true);
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(diffText).ConfigureAwait(true);
        }
    }
}
