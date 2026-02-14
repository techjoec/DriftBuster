using System.Threading;
using System.Threading.Tasks;

namespace DriftBuster.Gui.Services
{
    public interface ISessionCacheService
    {
        Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default);

        Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default);

        void Clear();
    }
}
