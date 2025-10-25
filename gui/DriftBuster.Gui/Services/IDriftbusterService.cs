using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;

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

        Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, CancellationToken cancellationToken = default);

        Task<ServerScanResponse> RunServerScansAsync(IEnumerable<ServerScanPlan> plans, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

        Task<ScheduleListResult> ListSchedulesAsync(CancellationToken cancellationToken = default);

        Task SaveSchedulesAsync(IEnumerable<ScheduleDefinition> schedules, CancellationToken cancellationToken = default);
    }
}
