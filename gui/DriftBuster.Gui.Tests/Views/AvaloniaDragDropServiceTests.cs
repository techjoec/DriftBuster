using System.Threading.Tasks;
using Avalonia.Input;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.Tests.Views;

public sealed class AvaloniaDragDropServiceTests
{
    [Fact]
    public void Instance_returns_singleton()
    {
        AvaloniaDragDropService.Instance.Should().BeSameAs(AvaloniaDragDropService.Instance);
    }

    [Fact]
    public async Task DoDragDropAsync_delegates_to_avalonia_dragdrop()
    {
        var action = () => AvaloniaDragDropService.Instance.DoDragDropAsync(null!, null!, DragDropEffects.None);
        await action.Should().ThrowAsync<System.Exception>();
    }
}
