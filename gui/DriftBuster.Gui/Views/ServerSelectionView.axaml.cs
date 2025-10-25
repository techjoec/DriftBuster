using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Views
{
    public partial class ServerSelectionView : UserControl
    {
        private const string ServerDragDataFormat = "driftbuster/server-slot";
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

        private async void OnCopyActivityRequested(object? sender, string text)
        {
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

#pragma warning disable 618
            var data = new DataObject();
            data.Set(ServerDragDataFormat, slot.HostId);
            data.Set(DataFormats.Text, slot.Label);
#pragma warning restore 618

            await DragDropService.DoDragDrop(e, data, DragDropEffects.Move).ConfigureAwait(true);
        }

        private void OnServerCardDragOver(object? sender, DragEventArgs e)
        {
            if (_viewModel is null || sender is not Control control || control.DataContext is not ServerSlotViewModel slot)
            {
                return;
            }

#pragma warning disable 618
            var sourceHostId = e.Data.Get(ServerDragDataFormat) as string;
#pragma warning restore 618
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

#pragma warning disable 618
            var sourceHostId = e.Data.Get(ServerDragDataFormat) as string;
#pragma warning restore 618
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
    }

#pragma warning disable 618
    internal interface IDragDropService
    {
        Task<DragDropEffects> DoDragDrop(PointerEventArgs args, IDataObject data, DragDropEffects effects);
    }

    internal sealed class AvaloniaDragDropService : IDragDropService
    {
        public static AvaloniaDragDropService Instance { get; } = new();

        public Task<DragDropEffects> DoDragDrop(PointerEventArgs args, IDataObject data, DragDropEffects effects)
        {
            return DragDrop.DoDragDrop(args, data, effects);
        }
    }
#pragma warning restore 618
}
