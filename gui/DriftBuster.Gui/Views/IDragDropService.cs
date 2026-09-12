using Avalonia.Input;

namespace DriftBuster.Gui.Views
{
    internal interface IDragDropService
    {
        Task<DragDropEffects> DoDragDropAsync(PointerPressedEventArgs args, IDataTransfer data, DragDropEffects effects);
    }
}
