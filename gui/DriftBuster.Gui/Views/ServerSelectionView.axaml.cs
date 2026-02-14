using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

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
    }
}
