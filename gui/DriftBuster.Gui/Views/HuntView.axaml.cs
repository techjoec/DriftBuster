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
    public partial class HuntView : UserControl
    {
        internal Func<Task<string?>>? FolderPickerOverride { get; set; }
        internal Func<Task<string?>>? FilePickerOverride { get; set; }
        internal IStorageProvider? StorageProviderOverride { get; set; }

        public HuntView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not HuntViewModel vm)
            {
                return;
            }

            if (FolderPickerOverride is not null || FilePickerOverride is not null)
            {
                var folderOverride = FolderPickerOverride is not null ? await FolderPickerOverride().ConfigureAwait(true) : null;
                var fileOverride = FilePickerOverride is not null ? await FilePickerOverride().ConfigureAwait(true) : null;
                var pathOverride = folderOverride ?? fileOverride;
                if (!string.IsNullOrEmpty(pathOverride))
                {
                    vm.DirectoryPath = pathOverride;
                }

                return;
            }

            var storageProvider = StorageProviderOverride ?? TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null)
            {
                return;
            }

            var folder = await PickFolderAsync(storageProvider).ConfigureAwait(true);
            var path = folder ?? await PickFileAsync(storageProvider).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(path))
            {
                vm.DirectoryPath = path;
            }
        }

        private static async Task<string?> PickFolderAsync(IStorageProvider storageProvider)
        {
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
            }).ConfigureAwait(true);

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        private static async Task<string?> PickFileAsync(IStorageProvider storageProvider)
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
            }).ConfigureAwait(true);

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
