using Avalonia.Input;

namespace DriftBuster.Gui.Views
{
    internal interface IDragDropService
    {
        Task<DragDropEffects> DoDragDropAsync(PointerEventArgs args, IDataTransfer data, DragDropEffects effects);
    }
}
