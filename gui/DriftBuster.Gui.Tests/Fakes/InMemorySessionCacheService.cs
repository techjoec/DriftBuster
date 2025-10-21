using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Fakes;

internal sealed class InMemorySessionCacheService : ISessionCacheService
{
    public ServerSelectionCache? Snapshot { get; set; }

    public bool Cleared { get; private set; }

    public Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Snapshot);
    }

    public Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
    {
        Snapshot = snapshot;
        return Task.CompletedTask;
    }

    public void Clear()
    {
        Cleared = true;
        Snapshot = null;
    }
}
