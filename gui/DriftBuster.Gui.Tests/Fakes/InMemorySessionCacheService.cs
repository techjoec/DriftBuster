using System;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Fakes;

internal sealed class InMemorySessionCacheService : ISessionCacheService
{
    private ConcurrentUpgradeHandle? _upgradeHandle;

    public ServerSelectionCache? Snapshot { get; set; }

    public bool Cleared { get; private set; }

    public Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync<ServerSelectionCache?>(static service => Task.FromResult(service.Snapshot), cancellationToken);
    }

    public Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(service =>
        {
            service.Snapshot = snapshot;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public void Clear()
    {
        Cleared = true;
        Snapshot = null;
    }

    public ConcurrentUpgradeHandle SimulateConcurrentUpgrade()
    {
        var handle = new ConcurrentUpgradeHandle();
        Volatile.Write(ref _upgradeHandle, handle);
        return handle;
    }

    private async Task ExecuteAsync(Func<InMemorySessionCacheService, Task> callback, CancellationToken cancellationToken)
    {
        await WaitForUpgradeAsync(cancellationToken).ConfigureAwait(false);
        await callback(this).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(Func<InMemorySessionCacheService, Task<T>> callback, CancellationToken cancellationToken)
    {
        await WaitForUpgradeAsync(cancellationToken).ConfigureAwait(false);
        return await callback(this).ConfigureAwait(false);
    }

    private async Task WaitForUpgradeAsync(CancellationToken cancellationToken)
    {
        var handle = Volatile.Read(ref _upgradeHandle);
        if (handle is null)
        {
            return;
        }

        await handle.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (handle.IsCompleted)
        {
            Interlocked.CompareExchange(ref _upgradeHandle, null, handle);
        }
    }

    internal sealed class ConcurrentUpgradeHandle
    {
        private readonly TaskCompletionSource<bool> _waitersStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _upgradeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitersStarted => _waitersStarted.Task;

        public bool IsCompleted => _upgradeReleased.Task.IsCompleted;

        public void Complete()
        {
            _upgradeReleased.TrySetResult(true);
        }

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            _waitersStarted.TrySetResult(true);

            if (_upgradeReleased.Task.IsCompleted)
            {
                await _upgradeReleased.Task.ConfigureAwait(false);
                return;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                await _upgradeReleased.Task.ConfigureAwait(false);
                return;
            }

            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static state =>
            {
                var source = (TaskCompletionSource<bool>)state!;
                source.TrySetCanceled();
            }, cancellation);

            var completed = await Task.WhenAny(_upgradeReleased.Task, cancellation.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }
}
