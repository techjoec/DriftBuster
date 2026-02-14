using Avalonia.Input;

namespace DriftBuster.Gui.Views
{
    internal sealed class AvaloniaDragDropService : IDragDropService
    {
        public static AvaloniaDragDropService Instance { get; } = new();

        public Task<DragDropEffects> DoDragDropAsync(PointerEventArgs args, IDataTransfer data, DragDropEffects effects)
        {
            return DragDrop.DoDragDropAsync(args, data, effects);
        }
    }
}
