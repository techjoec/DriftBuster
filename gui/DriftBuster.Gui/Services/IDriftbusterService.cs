using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Models;

namespace DriftBuster.Gui.Services
{
    public interface IDriftbusterService
    {
        Task<string> PingAsync(CancellationToken cancellationToken = default);

        Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default);

        Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default);

        Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default);

        Task SaveProfileAsync(RunProfileDefinition profile, CancellationToken cancellationToken = default);

        Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, CancellationToken cancellationToken = default);
    }
}
