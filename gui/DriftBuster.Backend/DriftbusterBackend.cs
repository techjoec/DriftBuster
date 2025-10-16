using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.FileSystemGlobbing;

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

        Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(
            RunProfileDefinition profile,
            OfflineCollectorRequest request,
            string? baseDir = null,
            CancellationToken cancellationToken = default);
    }

    [ExcludeFromCodeCoverage]
    public sealed class DriftbusterBackend : IDriftbusterBackend
    {
        private const int HuntSampleSize = 128 * 1024;
        private const string RedactedPlaceholder = "[REDACTED]";
        private const string SecretRulesResourceName = "DriftBuster.Backend.Resources.secret_rules.json";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private static readonly IReadOnlyList<HuntRuleDefinition> HuntRules = new[]
        {
            new HuntRuleDefinition(
                "server-name",
                "Potential hostnames, server names, or FQDN references",
                "server_name",
                new[] { "server", "host" },
                new[]
                {
                    new Regex(@"\b[a-z0-9_-]+\.(local|lan|corp|com|net|internal)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline),
                }),
            new HuntRuleDefinition(
                "certificate-thumbprint",
                "Likely certificate thumbprints",
                "certificate_thumbprint",
                new[] { "thumbprint", "certificate" },
                new[]
                {
                    new Regex(@"\b[0-9a-f]{40}\b", RegexOptions.IgnoreCase),
                    new Regex(@"\b[0-9a-f]{64}\b", RegexOptions.IgnoreCase),
                }),
            new HuntRuleDefinition(
                "version-number",
                "Version identifiers (semver style)",
                "version",
                new[] { "version" },
                new[]
                {
                    new Regex(@"\b\d+\.\d+\.\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase),
                }),
            new HuntRuleDefinition(
                "install-path",
                "Suspicious installation or directory paths",
                "install_path",
                new[] { "path", "install" },
                new[]
                {
                    new Regex(@"[A-Za-z]:\\\\[\\\\\w\-\. ]+", RegexOptions.IgnoreCase),
                    new Regex(@"/opt/[\w\-\.]+", RegexOptions.IgnoreCase),
                }),
        };

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

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunProfileManager.PrepareOfflineCollector(profile, request, baseDir, cancellationToken), cancellationToken);
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

            for (var index = 1; index < resolved.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidatePath = EnsureFile(resolved[index], false);
                var candidateName = Path.GetFileName(candidatePath);
                var candidateContent = ReadText(candidatePath);

                var plan = new DiffPlan
                {
                    Before = baselineContent,
                    After = candidateContent,
                    ContentType = "text",
                    FromLabel = baselineName,
                    ToLabel = candidateName,
                    Placeholder = RedactedPlaceholder,
                    ContextLines = 3,
                };

                comparisons.Add(new DiffComparison
                {
                    From = baselineName,
                    To = candidateName,
                    Plan = plan,
                    Metadata = new DiffMetadata
                    {
                        LeftPath = baselinePath,
                        RightPath = candidatePath,
                        ContentType = "text",
                        ContextLines = 3,
                    },
                });
            }

            var result = new DiffResult
            {
                Versions = resolved.ToArray(),
                Comparisons = comparisons.ToArray(),
            };

            result.RawJson = JsonSerializer.Serialize(result, SerializerOptions);
            return result;
        }

        private static string EnsureFile(string path, bool isBaseline)
        {
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
            string searchRoot;
            IReadOnlyCollection<string> files;

            if (File.Exists(rootPath))
            {
                searchRoot = Path.GetDirectoryName(rootPath) ?? Path.GetDirectoryName(Path.GetFullPath(rootPath)) ?? Path.GetPathRoot(rootPath)!;
                files = new[] { rootPath };
            }
            else if (Directory.Exists(rootPath))
            {
                searchRoot = rootPath;
                files = EnumerateFilesSafely(rootPath, cancellationToken).ToList();
            }
            else
            {
                throw new FileNotFoundException($"Path does not exist: {rootPath}");
            }

            var hits = new List<HuntHitRecord>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadSample(file, HuntSampleSize, out var text))
                {
                    continue;
                }

                foreach (var rule in HuntRules)
                {
                    if (!rule.MatchesKeywords(text))
                    {
                        continue;
                    }

                    hits.AddRange(rule.ExtractHits(text, file));
                }
            }

            var trimmedPattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
            if (!string.IsNullOrEmpty(trimmedPattern))
            {
                hits = hits.Where(hit => hit.Excerpt.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var materialisedHits = hits
                .Select(hit => ToModelHit(hit, searchRoot))
                .ToArray();

            var result = new HuntResult
            {
                Directory = rootPath,
                Pattern = trimmedPattern,
                Count = materialisedHits.Length,
                Hits = materialisedHits,
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

        private static bool TryReadSample(string path, int sampleSize, out string text)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(sampleSize, stream.Length);
            if (length == 0)
            {
                text = string.Empty;
                return true;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = stream.Read(buffer, 0, length);
                if (ContainsBinaryData(buffer.AsSpan(0, read)))
                {
                    text = string.Empty;
                    return false;
                }

                text = Utf8.GetString(buffer, 0, read);
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool ContainsBinaryData(ReadOnlySpan<byte> span)
        {
            foreach (var value in span)
            {
                if (value == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static HuntHit ToModelHit(HuntHitRecord hit, string searchRoot)
        {
            var relative = TryGetRelativePath(searchRoot, hit.Path);
            return new HuntHit
            {
                Rule = new HuntRuleSummary
                {
                    Name = hit.Rule.Name,
                    Description = hit.Rule.Description,
                    TokenName = hit.Rule.TokenName,
                    Keywords = hit.Rule.Keywords,
                    Patterns = hit.Rule.Patterns.Select(pattern => pattern.ToString()).ToArray(),
                },
                Path = hit.Path,
                RelativePath = relative,
                LineNumber = hit.LineNumber,
                Excerpt = hit.Excerpt,
            };
        }

        private static string TryGetRelativePath(string root, string target)
        {
            try
            {
                var relative = Path.GetRelativePath(root, target);
                if (!relative.StartsWith(".", StringComparison.Ordinal))
                {
                    return relative.Replace(Path.DirectorySeparatorChar, '/');
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return Path.GetFileName(target);
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

        private sealed record HuntHitRecord(HuntRuleDefinition Rule, string Path, int LineNumber, string Excerpt);

        [ExcludeFromCodeCoverage]
        private sealed record HuntRuleDefinition(string Name, string Description, string? TokenName, string[] Keywords, Regex[] Patterns)
        {
            public bool MatchesKeywords(string text)
            {
                if (Keywords.Length == 0)
                {
                    return true;
                }

                var lower = text.ToLowerInvariant();
                foreach (var keyword in Keywords)
                {
                    if (!lower.Contains(keyword, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            public IEnumerable<HuntHitRecord> ExtractHits(string text, string path)
            {
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    if (Keywords.Length > 0 &&
                        !Keywords.Any(keyword => line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    var matches = Patterns.Length == 0 || Patterns.Any(pattern => pattern.IsMatch(line));
                    if (!matches)
                    {
                        continue;
                    }

                    yield return new HuntHitRecord(this, path, index + 1, line.Trim());
                }
            }
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

                WriteMetadata(runDirectory, clean, runTimestamp, files, sources);

                return new RunProfileRunResult
                {
                    Profile = clean,
                    Timestamp = runTimestamp,
                    OutputDir = runDirectory,
                    Files = files.ToArray(),
                };
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

                    var configFileName = string.IsNullOrWhiteSpace(request.ConfigFileName)
                        ? $"{SafeName(clean.Name)}.offline.config.json"
                        : request.ConfigFileName.Trim();

                    if (!string.IsNullOrWhiteSpace(request.ConfigFileName))
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

                    var configPath = Path.Combine(tempRoot, configFileName);
                    var ruleset = LoadSecretRules(baseDir);
                    var payload = BuildOfflineConfigPayload(clean, request.Metadata, ruleset);
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(SerializerOptions)
                    {
                        WriteIndented = true,
                    });
                    File.WriteAllText(configPath, json + Environment.NewLine);

                    cancellationToken.ThrowIfCancellationRequested();

                    var scriptFileName = "driftbuster-offline-runner.ps1";
                    var scriptSource = ResolveRequiredFile(baseDir, "scripts", scriptFileName);
                    File.Copy(scriptSource, Path.Combine(tempRoot, scriptFileName), overwrite: true);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (File.Exists(packagePath))
                    {
                        File.Delete(packagePath);
                    }

                    ZipFile.CreateFromDirectory(tempRoot, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

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

            private static RunProfileDefinition CloneProfile(RunProfileDefinition profile)
            {
                NormaliseProfile(profile);
                return new RunProfileDefinition
                {
                    Name = profile.Name,
                    Description = profile.Description,
                    Baseline = profile.Baseline,
                    Sources = profile.Sources is null ? Array.Empty<string>() : profile.Sources.Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim()).ToArray(),
                    Options = new Dictionary<string, string>(profile.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal),
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

                var path = ResolveRequiredFile(baseDir, "src", "driftbuster", "secret_rules.json");
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                return document.RootElement.Clone();
            }

            private static object BuildOfflineConfigPayload(
                RunProfileDefinition profile,
                Dictionary<string, string>? metadata,
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

                var meta = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["profile_name"] = profile.Name,
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
