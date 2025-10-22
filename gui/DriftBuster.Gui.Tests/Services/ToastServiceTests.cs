using System;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Services;

public class ToastServiceTests
{
    [Fact]
    public void Show_adds_toast_to_collection()
    {
        var service = new ToastService(action => action());

        service.ActiveToasts.Should().BeEmpty();
        service.Show("Title", "Message", ToastLevel.Info, TimeSpan.FromMilliseconds(500));

        service.ActiveToasts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Toasts_auto_dismiss_after_duration()
    {
        var service = new ToastService(action => action());
        service.Show("Auto", "Dismiss", ToastLevel.Info, TimeSpan.FromMilliseconds(10));

        await Task.Delay(50);

        service.ActiveToasts.Should().BeEmpty();
    }

    [Fact]
    public void Dismiss_removes_toast_immediately()
    {
        var service = new ToastService(action => action());
        var toast = service.Show("Immediate", "Dismiss", ToastLevel.Info, TimeSpan.FromSeconds(10));

        service.ActiveToasts.Should().HaveCount(1);
        service.Dismiss(toast.Id);
        service.ActiveToasts.Should().BeEmpty();
    }

    [Fact]
    public void Overflow_moves_extra_toasts()
    {
        var service = new ToastService(action => action());

        for (var i = 0; i < 4; i++)
        {
            service.Show($"Toast {i}", "Message", ToastLevel.Info, TimeSpan.FromSeconds(5));
        }

        service.ActiveToasts.Should().HaveCount(3);
        service.OverflowToasts.Should().HaveCount(1);
    }

}
