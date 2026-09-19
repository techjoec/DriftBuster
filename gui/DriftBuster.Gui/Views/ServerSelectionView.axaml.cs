using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

using DriftBuster.Backend;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    public partial class ServerSelectionView : UserControl
    {
        private static readonly DataFormat<string> ServerDragDataFormat = DataFormat.CreateStringApplicationFormat("driftbuster.server-slot");
        private ServerSelectionViewModel? _viewModel;
        private readonly IDisposable _responsiveSubscription;

        internal IDragDropService DragDropService { get; set; } = AvaloniaDragDropService.Instance;

        public ServerSelectionView()
        {
            InitializeComponent();
            _responsiveSubscription = ResponsiveLayoutService.Attach(this, ResponsiveSpacingProfiles.ServerSelection);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
            {
                _viewModel.CopyActivityRequested -= OnCopyActivityRequested;
            }

            _viewModel = DataContext as ServerSelectionViewModel;
            if (_viewModel is not null)
            {
                _viewModel.CopyActivityRequested += OnCopyActivityRequested;
            }
        }

        // Saves a sign-in for the selected host's computer under the data root and points the host at it.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private async void OnSaveCredential(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not ServerSlotViewModel slot || !slot.IsRemote)
            {
                return;
            }

            var name = System.Text.RegularExpressions.Regex.Replace(slot.Computer.Trim(), "[^A-Za-z0-9._-]", "_", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            var path = System.IO.Path.Combine(DriftbusterPaths.GetDataRoot(), "credentials", name + ".xml");
            var saved = await DialogHost.ShowAsync<string>(this, new CredentialWindow(slot.Computer.Trim(), path)).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(saved))
            {
                slot.CredentialFile = saved;
            }
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private async void OnBrowseCredential(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not ServerSlotViewModel slot || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick a saved sign-in (Export-Clixml PSCredential)",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Credential file") { Patterns = ["*.xml"] }],
            }).ConfigureAwait(true);
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } local)
            {
                slot.CredentialFile = local;
            }
        }

        private void OnClearCredential(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is ServerSlotViewModel slot)
            {
                slot.CredentialFile = string.Empty;
            }
        }

        private async void OnCopyActivityRequested(object? sender, ValueEventArgs<string> e)
        {
            var text = e.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }

        private async void OnServerCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            await HandleServerCardPointerPressedAsync(sender, e).ConfigureAwait(true);
        }

        internal async Task HandleServerCardPointerPressedAsync(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel is null || _viewModel.IsBusy)
            {
                return;
            }

            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (sender is not Control control || control.DataContext is not ServerSlotViewModel slot)
            {
                return;
            }

            if (IsInteractivePointerSource(e.Source, control))
            {
                return;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ServerDragDataFormat, slot.HostId));
            data.Add(DataTransferItem.Create(DataFormat.Text, slot.Label));

            await DragDropService.DoDragDropAsync(e, data, DragDropEffects.Move).ConfigureAwait(true);
        }

        private void OnServerCardDragOver(object? sender, DragEventArgs e)
        {
            if (_viewModel is null || sender is not Control control || control.DataContext is not ServerSlotViewModel slot)
            {
                return;
            }

            var sourceHostId = ReadDragSlotId(e);
            var canAccept = _viewModel.CanAcceptReorder(sourceHostId, slot);
            e.DragEffects = canAccept ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnServerCardDrop(object? sender, DragEventArgs e)
        {
            if (_viewModel is null || sender is not Control control || control.DataContext is not ServerSlotViewModel slot)
            {
                return;
            }

            var sourceHostId = ReadDragSlotId(e);
            if (!_viewModel.CanAcceptReorder(sourceHostId, slot) || sourceHostId is null)
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var position = e.GetPosition(control);
            var insertBefore = position.Y <= control.Bounds.Height / 2;
            _viewModel.ReorderServer(sourceHostId, slot.HostId, insertBefore);
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }

        private static string? ReadDragSlotId(DragEventArgs e)
        {
            foreach (var item in e.DataTransfer.Items)
            {
                if (item is DataTransferItem concrete && concrete.TryGetRaw(ServerDragDataFormat) is string hostId)
                {
                    return hostId;
                }
            }

            return null;
        }

        private static bool IsInteractivePointerSource(object? source, Control card)
        {
            if (source is not Visual visual)
            {
                return false;
            }

            for (var current = visual; current is not null && current != card; current = current.GetVisualParent())
            {
                if (current is TextBox or ComboBox or Button or ToggleSwitch)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
