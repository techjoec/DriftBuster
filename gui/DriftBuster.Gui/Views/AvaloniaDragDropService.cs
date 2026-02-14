using System.Threading.Tasks;

using Avalonia.Input;

namespace DriftBuster.Gui.Views
{
#pragma warning disable 618
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
