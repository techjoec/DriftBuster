using System.Threading.Tasks;

using Avalonia.Input;

namespace DriftBuster.Gui.Views
{
#pragma warning disable 618
    internal interface IDragDropService
    {
        Task<DragDropEffects> DoDragDrop(PointerEventArgs args, IDataObject data, DragDropEffects effects);
    }
#pragma warning restore 618
}
