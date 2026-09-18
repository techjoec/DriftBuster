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
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new JsonStringEnumMemberConverter()
            },
        };

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
        {
            return Task.Run(
                () => new RunProfileListResult
                {
                    Profiles = RunProfileStore.ListProfiles(FacadeBaseDir(baseDir), cancellationToken).Select(profile => profile.ToDefinition()).ToArray(),
                },
                cancellationToken);
        }

        public Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileStore.SaveProfile(ToRunProfile(profile), FacadeBaseDir(baseDir)), cancellationToken);
        }

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => ToRunResult(RunProfileExecutor.ExecuteProfile(WithStoredOptionValues(ToRunProfile(profile), FacadeBaseDir(baseDir)), FacadeBaseDir(baseDir), timestamp, saveProfile, cancellationToken)),
                cancellationToken);
        }

        public Task<ScheduleListResult> ListSchedulesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ScheduleStore.ListSchedules(baseDir, cancellationToken), cancellationToken);
        }

        public Task SaveSchedulesAsync(IEnumerable<ScheduleDefinition> schedules, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ScheduleStore.SaveSchedules(schedules, baseDir, cancellationToken), cancellationToken);
        }

        public Task<ScheduleStatusListResult> ListScheduleStatusAsync(string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => new ScheduleStatusListResult
                {
                    Schedules = ScheduleCommands.List(FacadeBaseDir(baseDir), FacadePath(configPath), FacadePath(statePath))
                        .Cast<OrderedDictionary<string, object?>>()
                        .Select(ToScheduleStatus)
                        .ToArray(),
                },
                cancellationToken);
        }

        public Task<ScheduleDueResult> ListDueSchedulesAsync(string? reference = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => new ScheduleDueResult
                {
                    Runs = ScheduleCommands.Due(reference, FacadeBaseDir(baseDir), FacadePath(configPath), FacadePath(statePath))
                        .Cast<OrderedDictionary<string, object?>>()
                        .Select(run => new ScheduleDueRun
                        {
                            Name = (string)run["name"]!,
                            Profile = (string)run["profile"]!,
                            ScheduledFor = (string)run["scheduled_for"]!,
                            Tags = ((List<object?>)run["tags"]!).Cast<string>().ToArray(),
                            Metadata = new Dictionary<string, object?>((OrderedDictionary<string, object?>)run["metadata"]!, StringComparer.Ordinal),
                        })
                        .ToArray(),
                },
                cancellationToken);
        }

        public Task<ScheduleStateResult> CompleteScheduleAsync(string name, string? completedAt = null, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ToScheduleState(ScheduleCommands.MarkComplete(name, completedAt, FacadeBaseDir(baseDir), FacadePath(configPath), FacadePath(statePath))), cancellationToken);
        }

        public Task<ScheduleStateResult> SkipScheduleAsync(string name, string resumeAt, string? baseDir = null, string? configPath = null, string? statePath = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ToScheduleState(ScheduleCommands.SkipUntil(name, resumeAt, FacadeBaseDir(baseDir), FacadePath(configPath), FacadePath(statePath))), cancellationToken);
        }

        // A blank manifest or state path means the default under the profiles root.
        private static string? FacadePath(string? path) => string.IsNullOrWhiteSpace(path) ? null : path;

        private static ScheduleStatus ToScheduleStatus(OrderedDictionary<string, object?> entry) => new()
        {
            Name = (string)entry["name"]!,
            Profile = (string)entry["profile"]!,
            IntervalSeconds = (double)entry["interval_seconds"]!,
            Tags = ((List<object?>)entry["tags"]!).Cast<string>().ToArray(),
            Metadata = new Dictionary<string, object?>((OrderedDictionary<string, object?>)entry["metadata"]!, StringComparer.Ordinal),
            StartAt = (string?)entry["start_at"],
            NextRun = (string?)entry["next_run"],
            Pending = (string?)entry["pending"],
            Window = entry.GetValueOrDefault("window") is OrderedDictionary<string, object?> window
                ? new ScheduleWindowDefinition { Start = (string?)window["start"], End = (string?)window["end"], Timezone = (string?)window["timezone"] }
                : null,
        };

        private static ScheduleStateResult ToScheduleState(OrderedDictionary<string, object?> result) => new()
        {
            Name = (string)result["name"]!,
            NextRun = (string?)result["next_run"],
            Pending = (string?)result["pending"],
        };

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => OfflineCollectorWriter.Prepare(profile, request, baseDir, cancellationToken), cancellationToken);
        }

        // A blank base directory means the working directory, as the facade always treated it.
        private static string? FacadeBaseDir(string? baseDir) => string.IsNullOrWhiteSpace(baseDir) ? null : baseDir;

        // The GUI model as a run profile; the facade refuses a blank name before anything is validated or written.
        private static RunProfile ToRunProfile(RunProfileDefinition profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new InvalidOperationException("Profile name is required.");
            }

            return RunProfile.FromDefinition(profile);
        }

        // A structured profile runs with the option values its stored profile.json holds, as load_profile reads them (a list stays a list for
        // build_context), wherever the model's text for a key is still that value's str() text; an option that is new or edited runs with
        // its text. The stored file is read before the run saves over it.
        private static RunProfile WithStoredOptionValues(RunProfile profile, string? baseDir)
        {
            if (!profile.IsStructured || RunProfileStore.TryLoadStoredProfile(profile.Name, baseDir) is not { } stored)
            {
                return profile;
            }

            var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, text) in profile.Options)
            {
                var unchanged = stored.Options.TryGetValue(key, out var storedText) && string.Equals(storedText, text, StringComparison.Ordinal);
                options[key] = unchanged && stored.SecretOptions.TryGetValue(key, out var value) ? value : text;
            }

            return profile.WithSecretOptions(new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(options));
        }

        private static RunProfileRunResult ToRunResult(ProfileRunResult result) => new()
        {
            Profile = result.Profile.ToDefinition(),
            Timestamp = result.Timestamp,
            OutputDir = result.OutputDir,
            Files = result.Files
                .Select(file => new RunProfileFileResult
                {
                    Source = file.Source,
                    Destination = PathText.ToPosix(file.Destination),
                    Size = file.Size,
                    Sha256 = file.Sha256,
                })
                .ToArray(),
        };

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
                    var runner = new MultiServerRunner(PrepareMultiServerCacheDirectory(ResolveLegacyCacheRepositoryRoot()));
                    return runner.Run(planList.Select(MultiServerPlan.FromServerScanPlan), progress, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            ValidateMultiServerResponse(response);
            return response;
        }

        private static void ValidateMultiServerResponse(ServerScanResponse response)
            => MultiServerSchema.ValidateResponse(response);

        // The data root's diff cache, after copying the entries a checkout's legacy <repo>/artifacts/cache/diffs holds that the
        // cache lacks (best effort, before every scan), resolved as the scan's
        // cache_dir is: user home expanded, created, and every symlink and ".." followed physically.
        internal static string PrepareMultiServerCacheDirectory(string? repositoryRoot)
        {
            var cacheDirectory = DriftbusterPaths.GetCacheDirectory("diffs");
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                DiffCache.MigrateLegacyDiffCache(repositoryRoot, cacheDirectory);
            }

            return DiffCache.ResolveCacheDirectory(cacheDirectory, repositoryRoot: null);
        }

        // The checkout the working directory, the application base or the process directory lies in; the working directory when
        // none is inside one.
        private static string ResolveLegacyCacheRepositoryRoot()
            => RepositoryRoot.Find(Environment.CurrentDirectory)
                ?? RepositoryRoot.Find(AppContext.BaseDirectory)
                ?? RepositoryRoot.Find(Path.GetDirectoryName(Environment.ProcessPath))
                ?? Environment.CurrentDirectory;

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
            result.RawJson = JsonSerializer.Serialize(result, SerializerOptions);
            result.SanitizedJson = JsonSerializer.Serialize(summary, SerializerOptions);
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
            };
        }

        private static string EnsureFile(string path, bool isBaseline)
        {
            if (File.Exists(path) && !EnginePath.IsFile(path))
            {
                // A FIFO, socket or device: reading it could block forever (EnginePath.IsFile never opens it).
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
            var hits = scan.Hits.Select(hit => ToModelHit(hit, scan.RootDirectory));

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

            result.RawJson = JsonSerializer.Serialize(result, SerializerOptions);
            return result;
        }

        private static HuntHit ToModelHit(HuntFinding hit, string rootDirectory)
        {
            var transform = HuntEngine.PlanTransformForHit(hit, HuntEngine.DefaultPlaceholderTemplate);
            return new HuntHit
            {
                Rule = new HuntRuleSummary
                {
                    Name = hit.Rule.Name,
                    Description = hit.Rule.Description,
                    TokenName = hit.Rule.TokenName,
                    Keywords = hit.Rule.Keywords.ToArray(),
                    Patterns = hit.Rule.Patterns.Select(rulePattern => rulePattern.ToString()).ToArray(),
                },
                Path = hit.Path,
                RelativePath = HuntEngine.RelativeTo(hit.Path, rootDirectory) ?? PathText.Name(hit.Path),
                LineNumber = hit.LineNumber,
                Excerpt = hit.Excerpt,
                Metadata = transform is null
                    ? null
                    : new HuntHitMetadata
                    {
                        PlanTransform = new HuntPlanTransform
                        {
                            TokenName = transform.TokenName,
                            Value = transform.Value,
                            Placeholder = transform.Placeholder,
                            RuleName = transform.RuleName,
                        },
                    },
            };
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
