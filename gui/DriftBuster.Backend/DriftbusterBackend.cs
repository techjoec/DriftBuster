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
using Microsoft.Extensions.FileSystemGlobbing;

namespace DriftBuster.Backend
{
    [ExcludeFromCodeCoverage]
    public sealed partial class DriftbusterBackend : IDriftbusterBackend
    {
        private const string SecretRulesResourceName = "DriftBuster.Backend.Resources.secret_rules.json";
        private const string MultiServerModule = "driftbuster.multi_server";
        private const string MultiServerSchemaVersion = "multi-server.v1";

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
        private static readonly char[] GlobCharacters = { '*', '?', '[' };

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
            return Task.Run(() => RunProfileManager.ListProfiles(baseDir, cancellationToken), cancellationToken);
        }

        public Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.SaveProfile(profile, baseDir, cancellationToken), cancellationToken);
        }

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.RunProfile(profile, saveProfile, baseDir, timestamp, cancellationToken), cancellationToken);
        }

        public Task<ScheduleListResult> ListSchedulesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.ListSchedules(baseDir, cancellationToken), cancellationToken);
        }

        public Task SaveSchedulesAsync(IEnumerable<ScheduleDefinition> schedules, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.SaveSchedules(schedules, baseDir, cancellationToken), cancellationToken);
        }

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.PrepareOfflineCollector(profile, request, baseDir, cancellationToken), cancellationToken);
        }

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
                    Version = MultiServerSchemaVersion,
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

            var repositoryRoot = ResolveRepositoryRoot();
            var request = BuildMultiServerRequest(planList, repositoryRoot);
            var response = await ExecuteMultiServerAsync(request, progress, cancellationToken, repositoryRoot).ConfigureAwait(false);

            if (response is null)
            {
                throw new InvalidOperationException("Multi-server runner returned no payload.");
            }

            if (string.IsNullOrWhiteSpace(response.Version))
            {
                response.Version = MultiServerSchemaVersion;
            }

            ValidateMultiServerResponse(response);
            return response;
        }

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
            var baselineName = Path.GetFileName(baselinePath);
            var baselineContent = ReadText(baselinePath);

            var comparisons = new List<DiffComparison>();
            var artifacts = new List<DiffArtifact>();

            for (var index = 1; index < resolved.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (comparison, artifact) = BuildComparison(resolved[index], baselinePath, baselineName, baselineContent);
                comparisons.Add(comparison);
                artifacts.Add(artifact);
            }

            var result = new DiffResult
            {
                Versions = resolved.ToArray(),
                Comparisons = comparisons.ToArray(),
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

        private static (DiffComparison Comparison, DiffArtifact Artifact) BuildComparison(string candidateVersion, string baselinePath, string baselineName, string baselineContent)
        {
            var candidatePath = EnsureFile(candidateVersion, false);
            var candidateName = Path.GetFileName(candidatePath);
            var candidateContent = ReadText(candidatePath);

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

        private static string ResolveRepositoryRoot()
        {
            return ResolveRepositoryRoot(
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
                Path.GetDirectoryName(Environment.ProcessPath));
        }

        private static string ResolveRepositoryRoot(string currentDirectory, string appBaseDirectory, string? processDirectory)
        {
            foreach (var candidate in EnumerateRepositoryRootCandidates(currentDirectory, appBaseDirectory, processDirectory))
            {
                var resolved = FindRepositoryRoot(candidate);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return currentDirectory;
        }

        private static IEnumerable<string> EnumerateRepositoryRootCandidates(string currentDirectory, string appBaseDirectory, string? processDirectory)
        {
            if (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                yield return currentDirectory;
            }

            if (!string.IsNullOrWhiteSpace(appBaseDirectory) &&
                !string.Equals(currentDirectory, appBaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                yield return appBaseDirectory;
            }

            if (!string.IsNullOrWhiteSpace(processDirectory) &&
                !string.Equals(currentDirectory, processDirectory, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(appBaseDirectory, processDirectory, StringComparison.OrdinalIgnoreCase))
            {
                yield return processDirectory;
            }
        }

        private static string? FindRepositoryRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(startDirectory);
            }
            catch
            {
                return null;
            }

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")) ||
                    File.Exists(Path.Combine(current.FullName, "src", "driftbuster", "multi_server.py")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static MultiServerRequest BuildMultiServerRequest(List<ServerScanPlan> plans, string repositoryRoot)
        {
            if (plans is null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var cacheDirectory = DriftbusterPaths.GetCacheDirectory("diffs");
            MigrateLegacyDiffCache(repositoryRoot, cacheDirectory);
            return new MultiServerRequest
            {
                Plans = plans,
                CacheDirectory = cacheDirectory,
                SchemaVersion = MultiServerSchemaVersion,
            };
        }

        private async Task<ServerScanResponse> ExecuteMultiServerAsync(
            MultiServerRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken,
            string repositoryRoot)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var startInfo = BuildMultiServerProcessStartInfo(repositoryRoot);
            var requestJson = JsonSerializer.Serialize(request, SerializerOptions);

            using var process = new Process { StartInfo = startInfo };
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to launch Python process for multi-server runner.");
                }

                await SendRequestToProcessAsync(process, requestJson).ConfigureAwait(false);

                var response = await ParseMultiServerOutputAsync(process, progress, cancellationToken).ConfigureAwait(false);

                return response ?? new ServerScanResponse
                {
                    Version = MultiServerSchemaVersion,
                    Results = Array.Empty<ServerScanResult>(),
                    Catalog = Array.Empty<ConfigCatalogEntry>(),
                    Drilldown = Array.Empty<ConfigDrilldown>(),
                };
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static ProcessStartInfo BuildMultiServerProcessStartInfo(string repositoryRoot)
        {
            var pythonExecutable = ResolvePythonExecutable();
            var startInfo = CreatePythonStartInfo(pythonExecutable, repositoryRoot);
            var pythonPath = ResolvePythonPath(repositoryRoot);

            if (!string.IsNullOrWhiteSpace(pythonPath))
            {
                if (startInfo.Environment.TryGetValue("PYTHONPATH", out var configured) && !string.IsNullOrWhiteSpace(configured))
                {
                    if (!configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Contains(pythonPath, StringComparer.Ordinal))
                    {
                        startInfo.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPath, configured);
                    }
                }
                else
                {
                    var inherited = Environment.GetEnvironmentVariable("PYTHONPATH");
                    startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(inherited)
                        ? pythonPath
                        : string.Join(Path.PathSeparator, pythonPath, inherited);
                }
            }

            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            return startInfo;
        }

        private static async Task SendRequestToProcessAsync(Process process, string requestJson)
        {
            await process.StandardInput.WriteAsync(requestJson).ConfigureAwait(false);
            await process.StandardInput.WriteAsync(Environment.NewLine).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }

        private static async Task<ServerScanResponse?> ParseMultiServerOutputAsync(
            Process process,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

            ServerScanResponse? response = null;
            string? line;

            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                response = DispatchMultiServerMessage(line, progress, response);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"Python runner failed with exit code {process.ExitCode}: {stderr}");
            }

            return response;
        }

        private static ServerScanResponse? DispatchMultiServerMessage(
            string line,
            IProgress<ScanProgress>? progress,
            ServerScanResponse? response)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON from multi-server runner: {line}", ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return response;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (progress is not null && root.TryGetProperty("payload", out var payloadElement))
                    {
                        var update = payloadElement.Deserialize<ScanProgress>(SerializerOptions);
                        if (update is not null)
                        {
                            progress.Report(update);
                        }
                    }
                }
                else if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("payload", out var payloadElement))
                    {
                        response = payloadElement.Deserialize<ServerScanResponse>(SerializerOptions);
                    }
                }
                else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var message = root.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "Multi-server runner reported an error.";
                    throw new InvalidOperationException(message ?? "Multi-server runner reported an error.");
                }
            }

            return response;
        }

        private static string ResolvePythonExecutable()
        {
            var overridePath = Environment.GetEnvironmentVariable("DRIFTBUSTER_PYTHON");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            if (TryLocateExecutable("python3", out var python3))
            {
                return python3;
            }

            if (TryLocateExecutable("python", out var python))
            {
                return python;
            }

            return OperatingSystem.IsWindows() ? "python.exe" : "python3";
        }

        private static bool TryLocateExecutable(string name, out string fullPath)
        {
            if (Path.IsPathRooted(name))
            {
                fullPath = name;
                return File.Exists(name);
            }

            var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in entries)
            {
                var candidate = Path.Combine(entry, name);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }

                if (OperatingSystem.IsWindows())
                {
                    var exeCandidate = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? candidate
                        : candidate + ".exe";
                    if (File.Exists(exeCandidate))
                    {
                        fullPath = exeCandidate;
                        return true;
                    }
                }
            }

            fullPath = name;
            return false;
        }

        private static string ResolvePythonPath(string repositoryRoot)
        {
            var srcCandidate = Path.Combine(repositoryRoot, "src");
            if (Directory.Exists(Path.Combine(srcCandidate, "driftbuster")))
            {
                return srcCandidate;
            }

            if (Directory.Exists(Path.Combine(repositoryRoot, "driftbuster")))
            {
                return repositoryRoot;
            }

            return string.Empty;
        }

        private static ProcessStartInfo CreatePythonStartInfo(string pythonExecutable, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                StandardInputEncoding = Utf8,
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(MultiServerModule);
            return startInfo;
        }

        private static void MigrateLegacyDiffCache(string repositoryRoot, string cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                return;
            }

            try
            {
                var legacyRoot = Path.Combine(repositoryRoot, "artifacts", "cache", "diffs");
                if (!Directory.Exists(legacyRoot))
                {
                    return;
                }

                if (!Directory.Exists(cacheDirectory))
                {
                    Directory.CreateDirectory(cacheDirectory);
                }

                foreach (var file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(cacheDirectory, Path.GetFileName(file)!);
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target, overwrite: false);
                    }
                }
            }
            catch
            {
                // Migration is a best-effort convenience for developers; ignore failures.
            }
        }

        private static void ValidateMultiServerResponse(ServerScanResponse response)
        {
            if (response is null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (!string.Equals(response.Version, MultiServerSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported multi-server schema version '{response.Version}'. Expected '{MultiServerSchemaVersion}'.");
            }

            response.Results ??= Array.Empty<ServerScanResult>();
            response.Catalog ??= Array.Empty<ConfigCatalogEntry>();
            response.Drilldown ??= Array.Empty<ConfigDrilldown>();
            response.Summary ??= new ServerScanSummary
            {
                BaselineHostId = string.Empty,
                TotalHosts = response.Results.Length,
                ConfigsEvaluated = response.Catalog.Length,
                DriftingConfigs = response.Catalog.Count(entry => entry.DriftCount > 0),
                GeneratedAt = DateTimeOffset.UtcNow,
            };

            foreach (var result in response.Results)
            {
                result.Roots ??= Array.Empty<string>();
            }

            foreach (var entry in response.Catalog)
            {
                entry.PresentHosts ??= Array.Empty<string>();
                entry.MissingHosts ??= Array.Empty<string>();
            }

            foreach (var drilldown in response.Drilldown)
            {
                drilldown.Servers ??= Array.Empty<ConfigServerDetail>();
                drilldown.Notes ??= Array.Empty<string>();
            }
        }

        private sealed class MultiServerRequest
        {
            [JsonPropertyName("plans")]
            public List<ServerScanPlan> Plans { get; set; } = new();

            [JsonPropertyName("cache_dir")]
            public string CacheDirectory { get; set; } = string.Empty;

            [JsonPropertyName("schema_version")]
            public string SchemaVersion { get; set; } = MultiServerSchemaVersion;
        }

        private static string EnsureFile(string path, bool isBaseline)
        {
            if (File.Exists(path) && !PythonPath.IsFile(path))
            {
                // A FIFO, socket or device: reading it could block forever (PythonPath.IsFile never opens it).
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

        [ExcludeFromCodeCoverage]
        private static IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken cancellationToken)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();

                string[] files = Array.Empty<string>();
                string[] directories = Array.Empty<string>();

                try
                {
                    files = Directory.GetFiles(current);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                try
                {
                    directories = Directory.GetDirectories(current);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                foreach (var directory in directories)
                {
                    stack.Push(directory);
                }
            }
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
                    Patterns = hit.Rule.Patterns.Select(rulePattern => rulePattern.Pattern).ToArray(),
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

        private static string ResolvePath(string? value)
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

        [ExcludeFromCodeCoverage]
        private static class RunProfileManager
        {
            public static RunProfileListResult ListProfiles(string? baseDir, CancellationToken cancellationToken)
            {
                var root = ProfilesRoot(baseDir);
                var profiles = new List<RunProfileDefinition>();

                if (!Directory.Exists(root))
                {
                    Directory.CreateDirectory(root);
                }

                foreach (var profilePath in Directory.EnumerateFiles(root, "profile.json", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var text = File.ReadAllText(profilePath);
                        var profile = JsonSerializer.Deserialize<RunProfileDefinition>(text, SerializerOptions);
                        if (profile is not null)
                        {
                            NormaliseProfile(profile);
                            profiles.Add(profile);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (JsonException)
                    {
                    }
                }

                return new RunProfileListResult
                {
                    Profiles = profiles.ToArray(),
                };
            }

            public static void SaveProfile(RunProfileDefinition profile, string? baseDir, CancellationToken cancellationToken)
            {
                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                var root = ProfilesRoot(baseDir);
                Directory.CreateDirectory(root);

                var directory = Path.Combine(root, SafeName(clean.Name));
                Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, "profile.json");
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(clean, new JsonSerializerOptions(SerializerOptions)
                {
                    WriteIndented = true,
                });
                File.WriteAllText(path, json + Environment.NewLine);
            }

            public static RunProfileRunResult RunProfile(RunProfileDefinition profile, bool saveProfile, string? baseDir, string? timestamp, CancellationToken cancellationToken)
            {
                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                if (saveProfile)
                {
                    SaveProfile(clean, baseDir, cancellationToken);
                }

                var root = ProfilesRoot(baseDir);
                var profileDirectory = Path.Combine(root, SafeName(clean.Name));
                Directory.CreateDirectory(profileDirectory);

                var runTimestamp = timestamp ?? DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                var runDirectory = Path.Combine(profileDirectory, "raw", runTimestamp);
                Directory.CreateDirectory(runDirectory);

                var sources = new List<string>(clean.Sources ?? Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(clean.Baseline))
                {
                    var baselineIndex = sources.FindIndex(source => string.Equals(source, clean.Baseline, StringComparison.Ordinal));
                    if (baselineIndex > 0)
                    {
                        var baseline = sources[baselineIndex];
                        sources.RemoveAt(baselineIndex);
                        sources.Insert(0, baseline);
                    }
                }

                var files = CollectSourceFiles(sources, runDirectory, cancellationToken);

                WriteMetadata(runDirectory, clean, runTimestamp, files, sources);

                return new RunProfileRunResult
                {
                    Profile = clean,
                    Timestamp = runTimestamp,
                    OutputDir = runDirectory,
                    Files = files.ToArray(),
                };
            }

            private static List<RunProfileFileResult> CollectSourceFiles(List<string> sources, string runDirectory, CancellationToken cancellationToken)
            {
                var files = new List<RunProfileFileResult>();

                for (var index = 0; index < sources.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[index];
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        continue;
                    }

                    var matches = CollectMatches(source).ToList();
                    if (matches.Count == 0)
                    {
                        continue;
                    }

                    var destinationRoot = Path.Combine(runDirectory, $"source_{index:00}");
                    Directory.CreateDirectory(destinationRoot);

                    foreach (var match in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Directory.Exists(match))
                        {
                            foreach (var file in EnumerateFilesSafely(match, cancellationToken))
                            {
                                var entry = CopyFile(source, file, match, destinationRoot);
                                files.Add(entry);
                            }
                        }
                        else if (File.Exists(match))
                        {
                            var baseDirectory = Path.GetDirectoryName(match) ?? Path.GetDirectoryName(Path.GetFullPath(match)) ?? runDirectory;
                            var entry = CopyFile(source, match, baseDirectory, destinationRoot);
                            files.Add(entry);
                        }
                    }
                }

                return files;
            }

            public static OfflineCollectorResult PrepareOfflineCollector(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir, CancellationToken cancellationToken)
            {
                if (request is null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                var clean = CloneProfile(profile);
                if (string.IsNullOrWhiteSpace(clean.Name))
                {
                    throw new InvalidOperationException("Profile name is required.");
                }

                if (string.IsNullOrWhiteSpace(request.PackagePath))
                {
                    throw new InvalidOperationException("Package path is required.");
                }

                var packagePath = ResolvePath(request.PackagePath);
                var packageDirectory = Path.GetDirectoryName(packagePath);
                if (string.IsNullOrWhiteSpace(packageDirectory))
                {
                    throw new InvalidOperationException("Unable to resolve package directory.");
                }

                Directory.CreateDirectory(packageDirectory);

                var tempRoot = Path.Combine(Path.GetTempPath(), "DriftBusterOfflineCollector", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tempRoot);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var configFileName = ResolveConfigFileName(request.ConfigFileName, clean.Name);
                    var scriptFileName = "driftbuster-offline-runner.ps1";

                    WriteCollectorFiles(tempRoot, configFileName, scriptFileName, clean, request.Metadata, baseDir, packagePath, cancellationToken);

                    return new OfflineCollectorResult
                    {
                        PackagePath = packagePath,
                        ConfigFileName = configFileName,
                        ScriptFileName = scriptFileName,
                    };
                }
                finally
                {
                    TryDeleteDirectory(tempRoot);
                }
            }

            private static string ResolveConfigFileName(string? requestedName, string profileName)
            {
                var configFileName = string.IsNullOrWhiteSpace(requestedName)
                    ? $"{SafeName(profileName)}.offline.config.json"
                    : requestedName.Trim();

                if (!string.IsNullOrWhiteSpace(requestedName))
                {
                    var fileNameOnly = Path.GetFileName(configFileName);
                    if (!string.Equals(configFileName, fileNameOnly, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Config file name must not include path separators.");
                    }

                    if (fileNameOnly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        throw new InvalidOperationException("Config file name contains invalid characters.");
                    }

                    if (string.IsNullOrWhiteSpace(fileNameOnly))
                    {
                        throw new InvalidOperationException("Config file name is required.");
                    }

                    configFileName = fileNameOnly;
                }

                if (!configFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    configFileName += ".json";
                }

                return configFileName;
            }

            private static void WriteCollectorFiles(
                string tempRoot,
                string configFileName,
                string scriptFileName,
                RunProfileDefinition profile,
                IDictionary<string, string>? metadata,
                string? baseDir,
                string packagePath,
                CancellationToken cancellationToken)
            {
                var configPath = Path.Combine(tempRoot, configFileName);
                var ruleset = LoadSecretRules(baseDir);
                var payload = BuildOfflineConfigPayload(profile, metadata, ruleset);
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(SerializerOptions)
                {
                    WriteIndented = true,
                });
                File.WriteAllText(configPath, json + Environment.NewLine);

                cancellationToken.ThrowIfCancellationRequested();

                var scriptSource = ResolveRequiredFile(baseDir, "scripts", scriptFileName);
                File.Copy(scriptSource, Path.Combine(tempRoot, scriptFileName), overwrite: true);

                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                }

                ZipFile.CreateFromDirectory(tempRoot, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }

            public static ScheduleListResult ListSchedules(string? baseDir, CancellationToken cancellationToken)
            {
                var path = ScheduleManifestPath(baseDir);
                if (!File.Exists(path))
                {
                    return new ScheduleListResult();
                }

                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                JsonElement schedulesElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schedules", out var schedulesProperty))
                {
                    schedulesElement = schedulesProperty;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    schedulesElement = root;
                }
                else
                {
                    return new ScheduleListResult();
                }

                var entries = new List<ScheduleDefinition>();
                foreach (var element in schedulesElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (TryParseSchedule(element, out var schedule))
                    {
                        entries.Add(schedule);
                    }
                }

                return new ScheduleListResult
                {
                    Schedules = entries
                        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                };
            }

            public static void SaveSchedules(IEnumerable<ScheduleDefinition> schedules, string? baseDir, CancellationToken cancellationToken)
            {
                if (schedules is null)
                {
                    throw new ArgumentNullException(nameof(schedules));
                }

                var manifestPath = ScheduleManifestPath(baseDir);
                var manifestDirectory = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(manifestDirectory))
                {
                    Directory.CreateDirectory(manifestDirectory);
                }

                var payload = ValidateAndSerialiseSchedules(schedules, cancellationToken);

                var json = JsonSerializer.Serialize(
                    new Dictionary<string, object?>
(StringComparer.Ordinal)
                    {
                        ["schedules"] = payload,
                    },
                    new JsonSerializerOptions(SerializerOptions)
                    {
                        WriteIndented = true,
                    });

                if (!json.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                {
                    json += Environment.NewLine;
                }

                File.WriteAllText(manifestPath, json);
            }

            private static List<Dictionary<string, object?>> ValidateAndSerialiseSchedules(IEnumerable<ScheduleDefinition> schedules, CancellationToken cancellationToken)
            {
                var payload = new List<Dictionary<string, object?>>();
                foreach (var schedule in schedules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (schedule is null)
                    {
                        continue;
                    }

                    var name = schedule.Name?.Trim();
                    var profile = schedule.Profile?.Trim();
                    var every = schedule.Every?.Trim();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new InvalidOperationException("Schedule name is required.");
                    }

                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        throw new InvalidOperationException($"Schedule '{name}' is missing a profile reference.");
                    }

                    if (string.IsNullOrWhiteSpace(every))
                    {
                        throw new InvalidOperationException($"Schedule '{name}' is missing an interval.");
                    }

                    payload.Add(SerialiseSchedule(new ScheduleDefinition
                    {
                        Name = name,
                        Profile = profile,
                        Every = every,
                        StartAt = string.IsNullOrWhiteSpace(schedule.StartAt) ? null : schedule.StartAt.Trim(),
                        Window = NormaliseWindow(schedule.Window),
                        Tags = schedule.Tags ?? System.Array.Empty<string>(),
                        Metadata = schedule.Metadata ?? new Dictionary<string, string>(System.StringComparer.Ordinal),
                    }));
                }

                return payload;
            }

            private static bool TryParseSchedule(JsonElement element, out ScheduleDefinition schedule)
            {
                schedule = new ScheduleDefinition();
                if (!element.TryGetProperty("name", out var nameProperty))
                {
                    return false;
                }

                if (!element.TryGetProperty("profile", out var profileProperty))
                {
                    return false;
                }

                if (!element.TryGetProperty("every", out var everyProperty))
                {
                    return false;
                }

                var name = nameProperty.ToString().Trim();
                var profile = profileProperty.ToString().Trim();
                var every = everyProperty.ToString().Trim();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(every))
                {
                    return false;
                }

                schedule.Name = name;
                schedule.Profile = profile;
                schedule.Every = every;
                schedule.StartAt = element.TryGetProperty("start_at", out var startAtProperty)
                    ? startAtProperty.ToString()?.Trim()
                    : null;

                schedule.Window = ParseScheduleWindow(element);
                ParseScheduleMetadata(element, schedule);

                return true;
            }

            private static ScheduleWindowDefinition? ParseScheduleWindow(JsonElement element)
            {
                if (!element.TryGetProperty("window", out var windowProperty) || windowProperty.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var window = new ScheduleWindowDefinition
                {
                    Start = windowProperty.TryGetProperty("start", out var startProperty)
                        ? startProperty.ToString()?.Trim()
                        : null,
                    End = windowProperty.TryGetProperty("end", out var endProperty)
                        ? endProperty.ToString()?.Trim()
                        : null,
                    Timezone = windowProperty.TryGetProperty("timezone", out var timezoneProperty)
                        ? timezoneProperty.ToString()?.Trim()
                        : null,
                };

                if (!string.IsNullOrWhiteSpace(window.Start) || !string.IsNullOrWhiteSpace(window.End) || !string.IsNullOrWhiteSpace(window.Timezone))
                {
                    return window;
                }

                return null;
            }

            private static void ParseScheduleMetadata(JsonElement element, ScheduleDefinition schedule)
            {
                if (element.TryGetProperty("tags", out var tagsProperty))
                {
                    schedule.Tags = ExtractTags(tagsProperty);
                }

                if (element.TryGetProperty("metadata", out var metadataProperty) && metadataProperty.ValueKind == JsonValueKind.Object)
                {
                    var metadata = new Dictionary<string, string>(System.StringComparer.Ordinal);
                    foreach (var property in metadataProperty.EnumerateObject())
                    {
                        metadata[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                            ? string.Empty
                            : property.Value.ToString();
                    }

                    schedule.Metadata = metadata;
                }
            }

            private static ScheduleWindowDefinition? NormaliseWindow(ScheduleWindowDefinition? window)
            {
                if (window is null)
                {
                    return null;
                }

                var start = string.IsNullOrWhiteSpace(window.Start) ? null : window.Start.Trim();
                var end = string.IsNullOrWhiteSpace(window.End) ? null : window.End.Trim();
                var timezone = string.IsNullOrWhiteSpace(window.Timezone) ? null : window.Timezone.Trim();

                if (start is null && end is null && timezone is null)
                {
                    return null;
                }

                return new ScheduleWindowDefinition
                {
                    Start = start,
                    End = end,
                    Timezone = timezone,
                };
            }

            private static Dictionary<string, object?> SerialiseSchedule(ScheduleDefinition schedule)
            {
                var entry = new Dictionary<string, object?>(System.StringComparer.Ordinal)
                {
                    ["name"] = schedule.Name,
                    ["profile"] = schedule.Profile,
                    ["every"] = schedule.Every,
                };

                if (!string.IsNullOrWhiteSpace(schedule.StartAt))
                {
                    entry["start_at"] = schedule.StartAt;
                }

                SerialiseScheduleWindow(schedule.Window, entry);

                var cleanedTags = CleanTags(schedule.Tags);
                if (cleanedTags.Length > 0)
                {
                    entry["tags"] = cleanedTags;
                }

                if (schedule.Metadata is not null && schedule.Metadata.Count > 0)
                {
                    var metadata = new Dictionary<string, string>(System.StringComparer.Ordinal);
                    foreach (var pair in schedule.Metadata)
                    {
                        if (string.IsNullOrWhiteSpace(pair.Key))
                        {
                            continue;
                        }

                        metadata[pair.Key.Trim()] = pair.Value ?? string.Empty;
                    }

                    if (metadata.Count > 0)
                    {
                        entry["metadata"] = metadata;
                    }
                }

                return entry;
            }

            private static void SerialiseScheduleWindow(ScheduleWindowDefinition? window, Dictionary<string, object?> entry)
            {
                if (window is null)
                {
                    return;
                }

                var windowPayload = new Dictionary<string, string>(System.StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(window.Start))
                {
                    windowPayload["start"] = window.Start!;
                }

                if (!string.IsNullOrWhiteSpace(window.End))
                {
                    windowPayload["end"] = window.End!;
                }

                if (!string.IsNullOrWhiteSpace(window.Timezone))
                {
                    windowPayload["timezone"] = window.Timezone!;
                }

                if (windowPayload.Count > 0)
                {
                    entry["window"] = windowPayload;
                }
            }

            private static string[] CleanTags(string[]? tags)
            {
                var source = tags ?? System.Array.Empty<string>();
                return source
                    .Select(tag => tag?.Trim())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            private static string[] ExtractTags(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    return element.EnumerateArray()
                        .Select(tag => tag.ToString()?.Trim())
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .Select(tag => tag!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }

                var single = element.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(single)
                    ? System.Array.Empty<string>()
                    : new[] { single };
            }

            private static string ScheduleManifestPath(string? baseDir)
            {
                return Path.Combine(ProfilesRoot(baseDir), "schedules.json");
            }

            private static RunProfileDefinition CloneProfile(RunProfileDefinition profile)
            {
                NormaliseProfile(profile);
                return new RunProfileDefinition
                {
                    Name = profile.Name,
                    Description = profile.Description,
                    Baseline = profile.Baseline,
                    Sources = profile.Sources is null ? Array.Empty<string>() : profile.Sources.Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim()).ToArray(),
                    Options = new Dictionary<string, string>(profile.Options ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal),
                    SecretScanner = CloneSecretScanner(profile.SecretScanner),
                };
            }

            private static SecretScannerOptions CloneSecretScanner(SecretScannerOptions? options)
            {
                var clone = new SecretScannerOptions();
                if (options?.IgnoreRules is not null)
                {
                    clone.IgnoreRules = options.IgnoreRules
                        .Where(rule => !string.IsNullOrWhiteSpace(rule))
                        .Select(rule => rule.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
                if (options?.IgnorePatterns is not null)
                {
                    clone.IgnorePatterns = options.IgnorePatterns
                        .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                        .Select(pattern => pattern.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }

                return clone;
            }

            private static void NormaliseProfile(RunProfileDefinition profile)
            {
                profile.Sources ??= Array.Empty<string>();
                profile.Options ??= new Dictionary<string, string>(StringComparer.Ordinal);
                profile.SecretScanner = CloneSecretScanner(profile.SecretScanner);
            }

            private static JsonElement LoadSecretRules(string? baseDir)
            {
                var assembly = typeof(DriftbusterBackend).Assembly;
                using var resource = assembly.GetManifestResourceStream(SecretRulesResourceName);
                if (resource is not null)
                {
                    using var embedded = JsonDocument.Parse(resource);
                    return embedded.RootElement.Clone();
                }

                var path = ResolveRequiredFile(baseDir, "gui", "DriftBuster.Backend", "Resources", "secret_rules.json");
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                return document.RootElement.Clone();
            }

            private static object BuildOfflineConfigPayload(
                RunProfileDefinition profile,
                IDictionary<string, string>? metadata,
                JsonElement secretRules)
            {
                var sources = profile.Sources
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .Select(source => new { path = source })
                    .ToArray();

                var baseline = profile.Baseline;
                if (string.IsNullOrWhiteSpace(baseline) && sources.Length > 0)
                {
                    baseline = sources[0].path;
                }

                var meta = BuildOfflineMetadata(profile.Name, metadata);
                var secretScanner = profile.SecretScanner ?? new SecretScannerOptions();

                return new
                {
                    schema = "https://driftbuster.dev/offline-runner/config/v1",
                    version = "1",
                    profile = new
                    {
                        name = profile.Name,
                        description = profile.Description,
                        baseline,
                        sources,
                        tags = new[] { "offline" },
                        options = profile.Options,
                        secret_scanner = new
                        {
                            ignore_rules = secretScanner.IgnoreRules ?? Array.Empty<string>(),
                            ignore_patterns = secretScanner.IgnorePatterns ?? Array.Empty<string>(),
                            ruleset = secretRules,
                        },
                    },
                    runner = new
                    {
                        compress = true,
                        include_config = true,
                        include_logs = true,
                        include_manifest = true,
                        manifest_name = "manifest.json",
                        log_name = "runner.log",
                        data_directory_name = "data",
                        logs_directory_name = "logs",
                        package_name = $"{SafeName(profile.Name)}-offline-results",
                        cleanup_staging = true,
                    },
                    metadata = meta,
                };
            }

            private static Dictionary<string, object> BuildOfflineMetadata(string profileName, IDictionary<string, string>? metadata)
            {
                var meta = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["profile_name"] = profileName,
                    ["prepared_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };

                var user = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    meta["prepared_by"] = user;
                }

                if (metadata is not null)
                {
                    foreach (var entry in metadata)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key))
                        {
                            continue;
                        }

                        meta[entry.Key.Trim()] = entry.Value ?? string.Empty;
                    }
                }

                return meta;
            }

            private static string ResolveRequiredFile(string? baseDir, params string[] segments)
            {
                var relative = Path.Combine(segments);

                static bool Exists(string? candidate) => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate);

                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var fromBase = Path.Combine(baseDir, relative);
                    if (Exists(fromBase))
                    {
                        return Path.GetFullPath(fromBase);
                    }
                }

                var fromCurrent = Path.Combine(Environment.CurrentDirectory, relative);
                if (Exists(fromCurrent))
                {
                    return Path.GetFullPath(fromCurrent);
                }

                var fromApp = Path.Combine(AppContext.BaseDirectory, relative);
                if (Exists(fromApp))
                {
                    return Path.GetFullPath(fromApp);
                }

                var current = new DirectoryInfo(AppContext.BaseDirectory);
                while (current is not null)
                {
                    var candidate = Path.Combine(current.FullName, relative);
                    if (Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }

                    current = current.Parent;
                }

                throw new FileNotFoundException($"Unable to locate required asset '{relative}'.");
            }

            private static void TryDeleteDirectory(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            private static string SafeName(string text)
            {
                var builder = new StringBuilder(text.Length);
                foreach (var character in text)
                {
                    if (char.IsLetterOrDigit(character) || character is '-' or '_')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append('-');
                    }
                }

                return builder.ToString();
            }

            private static string ProfilesRoot(string? baseDir)
            {
                var root = string.IsNullOrWhiteSpace(baseDir) ? Environment.CurrentDirectory : baseDir;
                return Path.Combine(root, "Profiles");
            }

            private static IEnumerable<string> CollectMatches(string source)
            {
                var resolved = ResolvePath(source);
                if (File.Exists(resolved) || Directory.Exists(resolved))
                {
                    return new[] { resolved };
                }

                if (!ContainsGlob(source))
                {
                    throw new FileNotFoundException($"Path does not exist: {resolved}");
                }

                var (baseDirectory, pattern) = SplitGlob(resolved);
                if (!Directory.Exists(baseDirectory))
                {
                    return Array.Empty<string>();
                }

                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern);
                return matcher.GetResultsInFullPath(baseDirectory);
            }

            private static bool ContainsGlob(string value) => value.IndexOfAny(GlobCharacters) >= 0;

            private static (string BaseDirectory, string Pattern) SplitGlob(string absolutePattern)
            {
                var normalized = absolutePattern.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var root = Path.GetPathRoot(normalized) ?? string.Empty;
                var remainder = normalized[root.Length..];

                var segments = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var baseSegments = new List<string>();
                var index = 0;

                for (; index < segments.Length; index++)
                {
                    if (segments[index].IndexOfAny(GlobCharacters) >= 0)
                    {
                        break;
                    }

                    baseSegments.Add(segments[index]);
                }

                var baseDirectory = baseSegments.Count > 0
                    ? Path.Combine(root, Path.Combine(baseSegments.ToArray()))
                    : (string.IsNullOrEmpty(root) ? Environment.CurrentDirectory : root);

                baseDirectory = Path.GetFullPath(baseDirectory);

                var patternSegments = segments.Skip(index).ToArray();
                var pattern = patternSegments.Length > 0 ? Path.Combine(patternSegments) : "*";
                pattern = pattern.Replace(Path.DirectorySeparatorChar, '/');

                return (baseDirectory, pattern);
            }

            private static RunProfileFileResult CopyFile(string source, string file, string basePath, string destinationRoot)
            {
                var relative = GetRelativePath(file, basePath);
                var destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);

                var sha = ComputeSha256(destination);
                var size = new FileInfo(destination).Length;

                return new RunProfileFileResult
                {
                    Source = source,
                    Destination = destination.Replace(Path.DirectorySeparatorChar, '/'),
                    Size = size,
                    Sha256 = sha,
                };
            }

            private static string GetRelativePath(string file, string basePath)
            {
                try
                {
                    var relative = Path.GetRelativePath(basePath, file);
                    if (!relative.StartsWith(".", StringComparison.Ordinal))
                    {
                        return relative;
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (NotSupportedException)
                {
                }

                return Path.GetFileName(file);
            }

            private static string ComputeSha256(string path)
            {
                using var sha = SHA256.Create();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }

            private static void WriteMetadata(string runDirectory, RunProfileDefinition profile, string timestamp, IReadOnlyCollection<RunProfileFileResult> files, IReadOnlyList<string> orderedSources)
            {
                var baseline = profile.Baseline;
                if (string.IsNullOrWhiteSpace(baseline) && orderedSources.Count > 0)
                {
                    baseline = orderedSources[0];
                }

                var payload = new
                {
                    profile,
                    timestamp,
                    baseline,
                    files = files.Select(file => new
                    {
                        source = file.Source,
                        destination = file.Destination,
                        size = file.Size,
                        sha256 = file.Sha256,
                    }),
                };

                var metadataPath = Path.Combine(runDirectory, "metadata.json");
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(SerializerOptions)
                {
                    WriteIndented = true,
                });
                File.WriteAllText(metadataPath, json + Environment.NewLine);
            }
        }
    }
}
