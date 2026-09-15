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

        /// <summary>The manifest's schedules with their scheduler state (<c>schedule list</c>).</summary>
        Task<ScheduleStatusListResult> ListScheduleStatusAsync(string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default);

        /// <summary>The runs due at <paramref name="reference"/> (ISO 8601; now when empty), marked pending in the state file (<c>schedule due</c>).</summary>
        Task<ScheduleDueResult> ListDueSchedulesAsync(string? reference = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default);

        /// <summary>Completes a schedule's pending run at <paramref name="completedAt"/> (ISO 8601; the pending time when empty) (<c>schedule mark-complete</c>).</summary>
        Task<ScheduleStateResult> CompleteScheduleAsync(string name, string? completedAt = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default);

        /// <summary>Skips a schedule until <paramref name="resumeAt"/> (ISO 8601) (<c>schedule skip-until</c>).</summary>
        Task<ScheduleStateResult> SkipScheduleAsync(string name, string resumeAt, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default);

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
