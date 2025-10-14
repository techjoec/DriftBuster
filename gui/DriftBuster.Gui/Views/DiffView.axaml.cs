using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    public partial class DiffView : UserControl
    {
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
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
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

        private async void OnCopyRawJson(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not DiffViewModel vm || string.IsNullOrEmpty(vm.RawJson))
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(vm.RawJson).ConfigureAwait(true);
        }
    }
}
