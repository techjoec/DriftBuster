using System;
using System.Collections.Generic;
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

    [Fact]
    public void Buffered_dispatcher_batches_pending_actions()
    {
        var dispatcher = new BufferingDispatcher();
        var service = new ToastService(dispatcher.Enqueue);

        service.Show("One", "Message", ToastLevel.Info, TimeSpan.FromSeconds(5));
        service.Show("Two", "Message", ToastLevel.Info, TimeSpan.FromSeconds(5));

        dispatcher.DispatchCount.Should().Be(1);
        service.ActiveToasts.Should().BeEmpty();

        dispatcher.FlushAll();

        service.ActiveToasts.Should().HaveCount(2);
        dispatcher.DispatchCount.Should().Be(1);
    }

    [Fact]
    public void Buffered_dispatcher_allows_inline_dismiss_without_extra_dispatch()
    {
        var dispatcher = new BufferingDispatcher();
        var service = new ToastService(dispatcher.Enqueue);

        var toast = service.Show("ToRemove", "Message", ToastLevel.Info, TimeSpan.FromSeconds(5));
        service.Dismiss(toast.Id);

        dispatcher.DispatchCount.Should().Be(1);

        dispatcher.FlushAll();

        service.ActiveToasts.Should().BeEmpty();
    }

    [Fact]
    public async Task Buffered_dispatcher_processes_auto_dismiss_async()
    {
        var dispatcher = new BufferingDispatcher();
        var service = new ToastService(dispatcher.Enqueue);

        service.Show("Auto", "Dismiss", ToastLevel.Info, TimeSpan.FromMilliseconds(25));

        dispatcher.FlushAll();
        service.ActiveToasts.Should().HaveCount(1);

        await Task.Delay(60);

        dispatcher.FlushAll();

        service.ActiveToasts.Should().BeEmpty();
    }

    private sealed class BufferingDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int DispatchCount { get; private set; }

        public void Enqueue(Action action)
        {
            DispatchCount++;
            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }

        public void FlushAll()
        {
            while (true)
            {
                Action? next;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    next = _queue.Dequeue();
                }

                next();
            }
        }
    }
}
