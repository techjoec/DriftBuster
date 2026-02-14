using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend
{
    public interface IDriftbusterBackend
    {
        Task<string> PingAsync(CancellationToken cancellationToken = default);

        Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default);

        Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default);

        Task<RunProfileListResult> ListProfilesAsync(string? baseDir = null, CancellationToken cancellationToken = default);

        Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default);

        Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default);

        Task<ScheduleListResult> ListSchedulesAsync(string? baseDir = null, CancellationToken cancellationToken = default);

        Task SaveSchedulesAsync(IEnumerable<ScheduleDefinition> schedules, string? baseDir = null, CancellationToken cancellationToken = default);

        Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(
            RunProfileDefinition profile,
            OfflineCollectorRequest request,
            string? baseDir = null,
            CancellationToken cancellationToken = default);

        Task<ServerScanResponse> RunServerScansAsync(
            IEnumerable<ServerScanPlan> plans,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
