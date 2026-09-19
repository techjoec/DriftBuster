using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input.Platform;
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

        private HuntViewModel.HuntHitView? Selected => (DataContext as HuntViewModel)?.SelectedHit;

        private async Task CopyAsync(string? text)
        {
            if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }

        private async void OnCopyLocation(object? sender, RoutedEventArgs e) => await CopyAsync(Selected?.Location).ConfigureAwait(true);

        private async void OnCopyExcerpt(object? sender, RoutedEventArgs e) => await CopyAsync(Selected?.FullExcerpt).ConfigureAwait(true);

        private void OnOpenFolder(object? sender, RoutedEventArgs e) => OpenFolder(Selected);

        // Opens the folder holding the finding's file in the system file manager.
        private static void OpenFolder(HuntViewModel.HuntHitView? hit)
        {
            var folder = hit is null ? null : System.IO.Path.GetDirectoryName(hit.FullPath);
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing to open with; the path is still shown and can be copied.
            }
        }

        // Right-click on a finding: copy it, narrow to its rule or file, open its folder, or report it as a false positive.
        private void OnHitContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
        {
            if (DataContext is not HuntViewModel vm
                || (e.Source as Control)?.DataContext is not HuntViewModel.HuntHitView hit
                || sender is not Control grid)
            {
                return;
            }

            vm.SelectedHit = hit;
            MenuItem Item(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action();
                return item;
            }

            MenuItem Sub(string header, params MenuItem[] items) => new() { Header = header, ItemsSource = items };
            var menu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    Sub("Copy",
                        Item("Location (file:line)", () => _ = CopyAsync(hit.Location)),
                        Item("File path", () => _ = CopyAsync(hit.RelativePath)),
                        Item("Excerpt", () => _ = CopyAsync(hit.FullExcerpt)),
                        Item("As JSON", () => _ = CopyAsync(hit.ToJson())),
                        Item("As TSV", () => _ = CopyAsync(hit.ToTsv()))),
                    Item($"Show only {hit.RuleName}", () => vm.RuleFilter = hit.RuleName),
                    Item("Show only this file", () => vm.FileFilter = hit.RelativePath),
                    Item("Open folder", () => OpenFolder(hit)),
                    new Separator(),
                    Item("Report false positive…", () => _ = ShowBugReportAsync(hit)),
                },
            };
            grid.ContextMenu = menu;
            menu.Open(grid);
            e.Handled = true;
        }

        private async Task ShowBugReportAsync(HuntViewModel.HuntHitView hit)
        {
            var window = new BugReportWindow(hit.BugReport(), preferApi: false);
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await window.ShowDialog(owner).ConfigureAwait(true);
            }
            else
            {
                window.Show();
            }
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
