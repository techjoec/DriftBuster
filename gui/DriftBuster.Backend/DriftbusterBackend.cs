using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend
{
    [ExcludeFromCodeCoverage]
    public sealed partial class DriftbusterBackend : IDriftbusterBackend
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        public Task<string> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("pong");
        }

        public Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BuildDiffResult(versions, cancellationToken), cancellationToken);
        }

        public Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BuildHuntResult(directory, pattern, cancellationToken), cancellationToken);
        }

        public Task<RunProfileListResult> ListProfilesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
            => Task.Run(() => new RunProfileListResult(RunProfileStore.List(FacadeBaseDir(baseDir), cancellationToken)), cancellationToken);

        public Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default)
            => Task.Run(() => RunProfileStore.Save(profile, FacadeBaseDir(baseDir)), cancellationToken);

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
            => Task.Run(() => new RunProfileExecutor(FacadeBaseDir(baseDir)).Execute(profile, saveProfile, timestamp, cancellationToken), cancellationToken);

        public Task<ScheduleListResult> ListSchedulesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
            => Task.Run(() => ScheduleStore.ListSchedules(baseDir), cancellationToken);

        public Task SaveSchedulesAsync(IEnumerable<ScheduleDefinition> schedules, string? baseDir = null, CancellationToken cancellationToken = default)
            => Task.Run(() => ScheduleStore.SaveSchedules(schedules, baseDir), cancellationToken);

        public Task<ScheduleStatusListResult> ListScheduleStatusAsync(string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Schedules(baseDir, configPath, statePath).List(), cancellationToken);

        public Task<ScheduleDueResult> ListDueSchedulesAsync(DateTimeOffset? reference = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Schedules(baseDir, configPath, statePath).Due(reference), cancellationToken);

        public Task<ScheduleStateResult> CompleteScheduleAsync(string name, DateTimeOffset? completedAt = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Schedules(baseDir, configPath, statePath).MarkComplete(name, completedAt), cancellationToken);

        public Task<ScheduleStateResult> SkipScheduleAsync(string name, DateTimeOffset resumeAt, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Schedules(baseDir, configPath, statePath).SkipUntil(name, resumeAt), cancellationToken);

        private static ScheduleCommands Schedules(string? baseDir, string? configPath, string? statePath)
            => new(FacadeBaseDir(baseDir), FacadePath(configPath), FacadePath(statePath));

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => OfflineCollectorWriter.Prepare(profile, request, baseDir, cancellationToken), cancellationToken);
        }

        // A blank manifest or state path means the default under the profiles root.
        private static string? FacadePath(string? path) => string.IsNullOrWhiteSpace(path) ? null : path;

        // A blank base directory means the working directory.
        private static string? FacadeBaseDir(string? baseDir) => string.IsNullOrWhiteSpace(baseDir) ? null : baseDir;

        public async Task<ServerScanResponse> RunServerScansAsync(
            IEnumerable<ServerScanPlan> plans,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plans is null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var planList = plans.Select(ClonePlan).ToList();
            if (planList.Count == 0)
            {
                return new ServerScanResponse
                {
                    Version = MultiServerSchema.Version,
                    Results = Array.Empty<ServerScanResult>(),
                    Catalog = Array.Empty<ConfigCatalogEntry>(),
                    Drilldown = Array.Empty<ConfigDrilldown>(),
                    Summary = new ServerScanSummary
                    {
                        BaselineHostId = string.Empty,
                        TotalHosts = 0,
                        ConfigsEvaluated = 0,
                        DriftingConfigs = 0,
                        GeneratedAt = DateTimeOffset.UtcNow,
                    },
                };
            }

            InitializePlans(planList, progress, cancellationToken);

            // The scan runs in process on a pool thread; progress is reported inline on that thread by the runner.
            var response = await Task.Run(
                () =>
                {
                    var runner = new MultiServerRunner(DriftbusterPaths.GetCacheDirectory("diffs"));
                    return runner.Run(planList.Select(MultiServerPlan.FromServerScanPlan), progress, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            ValidateMultiServerResponse(response);
            return response;
        }

        private static void ValidateMultiServerResponse(ServerScanResponse response)
            => MultiServerSchema.ValidateResponse(response);

        private static void InitializePlans(List<ServerScanPlan> planList, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            foreach (var plan in planList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(plan.HostId))
                {
                    plan.HostId = Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(plan.Label))
                {
                    plan.Label = plan.HostId;
                }

                plan.Baseline ??= new ServerScanBaselinePreference();
                plan.Export ??= new ServerScanExportOptions();

                progress?.Report(new ScanProgress
                {
                    HostId = plan.HostId,
                    Status = ServerScanStatus.Queued,
                    Message = "Queued",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
        }

        private static DiffResult BuildDiffResult(IEnumerable<string?> versions, CancellationToken cancellationToken)
        {
            var versionList = versions?.ToList() ?? new List<string?>();
            if (versionList.Count < 2)
            {
                throw new InvalidOperationException("Provide at least two file paths via 'versions'.");
            }

            var resolved = versionList
                .Select(ResolvePath)
                .ToList();

            var baselinePath = EnsureFile(resolved[0], true);
            var baselineContent = ReadText(baselinePath);

            var comparisons = new List<DiffComparison>();
            var artifacts = new List<DiffArtifact>();

            for (var index = 1; index < resolved.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (comparison, artifact) = BuildComparison(resolved[index], baselinePath, baselineContent);
                comparisons.Add(comparison);
                artifacts.Add(artifact);
            }

            var result = new DiffResult
            {
                Versions = resolved.ToArray(),
                Comparisons = comparisons.ToArray(),
                Settings = SettingsComparisonBuilder.CompareFiles(
                    [(baselinePath, baselineContent), .. comparisons.Select(comparison => (comparison.Metadata.RightPath, ReadText(comparison.Metadata.RightPath)))],
                    cancellationToken),
            };

            var fileNames = resolved
                .Select(path => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path))
                .ToArray();
            var summary = DiffBuilder.SummariseDiffResults(artifacts, fileNames);
            result.Summary = summary;
            result.RawJson = ModelJson.Serialize(result);
            result.SanitizedJson = ModelJson.Serialize(summary);
            return result;
        }

        private static (DiffComparison Comparison, DiffArtifact Artifact) BuildComparison(string candidateVersion, string baselinePath, string baselineContent)
        {
            var candidatePath = EnsureFile(candidateVersion, false);
            var candidateContent = ReadText(candidatePath);
            var (baselineName, candidateName) = PathText.DistinctNames(baselinePath, candidatePath);

            var contentType = ContentTypeResolver.ResolvePair(baselinePath, candidatePath);
            var artifact = DiffBuilder.BuildUnifiedDiff(baselineContent, candidateContent, contentType, baselineName, candidateName, contextLines: 3);

            var plan = new DiffPlan
            {
                Before = artifact.CanonicalBefore,
                After = artifact.CanonicalAfter,
                ContentType = contentType,
                FromLabel = baselineName,
                ToLabel = candidateName,
                Placeholder = artifact.Placeholder,
                ContextLines = artifact.ContextLines,
            };

            var comparison = new DiffComparison
            {
                From = baselineName,
                To = candidateName,
                Plan = plan,
                Metadata = new DiffMetadata
                {
                    LeftPath = baselinePath,
                    RightPath = candidatePath,
                    ContentType = contentType,
                    ContextLines = artifact.ContextLines,
                },
                UnifiedDiff = artifact.Diff,
            };
            return (comparison, artifact);
        }

        private static ServerScanPlan ClonePlan(ServerScanPlan plan)
        {
            if (plan is null)
            {
                return new ServerScanPlan();
            }

            return new ServerScanPlan
            {
                HostId = plan.HostId,
                Label = plan.Label,
                Scope = plan.Scope,
                Roots = plan.Roots?.ToArray() ?? Array.Empty<string>(),
                Baseline = plan.Baseline is null
                    ? new ServerScanBaselinePreference()
                    : new ServerScanBaselinePreference
                    {
                        IsPreferred = plan.Baseline.IsPreferred,
                        Priority = plan.Baseline.Priority,
                        Role = string.IsNullOrWhiteSpace(plan.Baseline.Role) ? "auto" : plan.Baseline.Role,
                    },
                Export = plan.Export is null
                    ? new ServerScanExportOptions()
                    : new ServerScanExportOptions
                    {
                        IncludeCatalog = plan.Export.IncludeCatalog,
                        IncludeDrilldown = plan.Export.IncludeDrilldown,
                        IncludeDiffs = plan.Export.IncludeDiffs,
                        IncludeSummary = plan.Export.IncludeSummary,
                    },
                ThrottleSeconds = plan.ThrottleSeconds,
                CachedAt = plan.CachedAt,
                Registry = plan.Registry is null
                    ? null
                    : new ServerScanRegistryOptions
                    {
                        Keys = plan.Registry.Keys?.ToArray() ?? Array.Empty<string>(),
                        Computer = plan.Registry.Computer,
                        CredentialFile = plan.Registry.CredentialFile,
                    },
            };
        }

        private static string EnsureFile(string path, bool isBaseline)
        {
            if (File.Exists(path) && !FilePaths.IsFile(path))
            {
                // A FIFO, socket or device: reading it could block forever (FilePaths.IsFile never opens it).
                throw new InvalidOperationException(isBaseline
                    ? $"Baseline path is not a regular file: {path}"
                    : $"Comparison path is not a regular file: {path}");
            }

            if (!File.Exists(path))
            {
                if (Directory.Exists(path))
                {
                    throw new InvalidOperationException(isBaseline
                        ? $"Baseline path is not a file: {path}"
                        : $"Comparison path is not a file: {path}");
                }

                throw new FileNotFoundException($"Path does not exist: {path}");
            }

            return path;
        }

        private static string ReadText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static HuntResult BuildHuntResult(string? directory, string? pattern, CancellationToken cancellationToken)
        {
            var rootPath = ResolvePath(directory);
            if (!File.Exists(rootPath) && !Directory.Exists(rootPath))
            {
                throw new FileNotFoundException($"Path does not exist: {rootPath}");
            }

            var scan = HuntEngine.HuntPath(rootPath, Hunt.HuntRules.Default, cancellationToken: cancellationToken);
            IEnumerable<HuntHit> hits = HuntEngine.ToHits(scan);

            var trimmedPattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
            if (!string.IsNullOrEmpty(trimmedPattern))
            {
                hits = hits.Where(hit => hit.Excerpt.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase));
            }

            var materialisedHits = hits.ToArray();
            var result = new HuntResult
            {
                Directory = rootPath,
                Pattern = trimmedPattern,
                Count = materialisedHits.Length,
                Hits = materialisedHits,
                UnreadableFiles = scan.UnreadableFiles.Count > 0 ? scan.UnreadableFiles.ToArray() : null,
            };

            result.RawJson = ModelJson.Serialize(result);
            return result;
        }

        internal static string ResolvePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Path is required.");
            }

            var expanded = Environment.ExpandEnvironmentVariables(value);

            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    throw new InvalidOperationException("Unable to resolve '~' because the home directory is unknown.");
                }

                expanded = Path.Combine(home, expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return Path.GetFullPath(expanded);
        }
    }
}
