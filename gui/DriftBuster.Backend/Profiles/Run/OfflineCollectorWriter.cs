using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Packages a run profile for <c>scripts/driftbuster-offline-runner.ps1</c>: a zip holding the runner script and an offline runner
/// config (<c>https://driftbuster.dev/offline-runner/config/v1</c>) carrying the profile, its structured sources, the packaged secret
/// ruleset and the runner settings.
/// </summary>
public static class OfflineCollectorWriter
{
    public const string ScriptFileName = "driftbuster-offline-runner.ps1";

    /// <summary>Writes the collector package to <see cref="OfflineCollectorRequest.PackagePath"/> and names the files inside it.</summary>
    public static OfflineCollectorResult Prepare(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clean = CloneProfile(profile);
        if (string.IsNullOrWhiteSpace(clean.Name))
        {
            throw new InvalidOperationException("Profile name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.PackagePath))
        {
            throw new InvalidOperationException("Package path is required.");
        }

        var packagePath = DriftbusterBackend.ResolvePath(request.PackagePath);
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
            WriteCollectorFiles(tempRoot, configFileName, clean, request.Metadata, baseDir, packagePath, cancellationToken);
            return new OfflineCollectorResult(packagePath, configFileName, ScriptFileName);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>The offline runner config for <paramref name="profile"/>; the baseline defaults to the first source's path.</summary>
    public static OfflineRunnerConfig BuildConfig(RunProfileDefinition profile, IDictionary<string, string>? metadata, JsonElement secretRules, DateTimeOffset preparedAt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var baseline = string.IsNullOrWhiteSpace(profile.Baseline) && profile.Sources.Count > 0 ? profile.Sources[0].Path : profile.Baseline;
        return new OfflineRunnerConfig(
            OfflineRunnerConfig.SchemaId,
            "1",
            new OfflineRunnerProfile(
                profile.Name,
                profile.Description,
                baseline,
                profile.Sources,
                ["offline"],
                profile.Options,
                new OfflineRunnerSecretScanner(profile.SecretScanner.IgnoreRules, profile.SecretScanner.IgnorePatterns, secretRules)),
            new OfflineRunnerSettings(
                Compress: true,
                IncludeConfig: true,
                IncludeLogs: true,
                IncludeManifest: true,
                ManifestName: "manifest.json",
                LogName: "runner.log",
                DataDirectoryName: "data",
                LogsDirectoryName: "logs",
                PackageName: $"{RunProfileStore.SafeName(profile.Name)}-offline-results",
                CleanupStaging: true),
            BuildMetadata(profile.Name, metadata, preparedAt));
    }

    private static string ResolveConfigFileName(string? requestedName, string profileName)
    {
        var configFileName = string.IsNullOrWhiteSpace(requestedName)
            ? $"{RunProfileStore.SafeName(profileName)}.offline.config.json"
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
        RunProfileDefinition profile,
        IDictionary<string, string>? metadata,
        string? baseDir,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(tempRoot, configFileName);
        File.WriteAllText(configPath, ModelJson.Serialize(BuildConfig(profile, metadata, LoadSecretRules(baseDir), DateTimeOffset.UtcNow)));

        cancellationToken.ThrowIfCancellationRequested();

        var scriptSource = ResolveRequiredFile(baseDir, "scripts", ScriptFileName);
        File.Copy(scriptSource, Path.Combine(tempRoot, ScriptFileName), overwrite: true);

        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        ZipFile.CreateFromDirectory(tempRoot, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    // The profile with blank sources dropped, paths and aliases trimmed and the ignore lists cleaned. Exclude patterns are kept as
    // written, as the run executor matches them, so a profile excludes the same files when run and when collected.
    private static RunProfileDefinition CloneProfile(RunProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with
        {
            Sources = [.. profile.Sources
                .Where(source => !string.IsNullOrWhiteSpace(source.Path))
                .Select(source => source with { Path = source.Path.Trim(), Alias = string.IsNullOrWhiteSpace(source.Alias) ? null : source.Alias.Trim() })],
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = RunProfileCommands.CleanValues(profile.SecretScanner.IgnoreRules),
                IgnorePatterns = RunProfileCommands.CleanValues(profile.SecretScanner.IgnorePatterns),
            },
        };
    }

    private static JsonElement LoadSecretRules(string? baseDir)
    {
        using var resource = typeof(OfflineCollectorWriter).Assembly.GetManifestResourceStream(SecretScanner.SecretRulesResource);
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

    private static Dictionary<string, string> BuildMetadata(string profileName, IDictionary<string, string>? metadata, DateTimeOffset preparedAt)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile_name"] = profileName,
            ["prepared_at"] = preparedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            meta["prepared_by"] = Environment.UserName;
        }

        foreach (var (key, value) in metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                meta[key.Trim()] = value ?? string.Empty;
            }
        }

        return meta;
    }

    /// <summary>
    /// The first existing <paramref name="segments"/> path under <paramref name="baseDir"/>, the working directory, the application
    /// base directory, or any directory above the application base.
    /// </summary>
    internal static string ResolveRequiredFile(string? baseDir, params string[] segments)
    {
        var relative = Path.Combine(segments);

        static bool Exists(string? candidate) => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate);

        if (!string.IsNullOrWhiteSpace(baseDir) && Exists(Path.Combine(baseDir, relative)))
        {
            return Path.GetFullPath(Path.Combine(baseDir, relative));
        }

        if (Exists(Path.Combine(Environment.CurrentDirectory, relative)))
        {
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relative));
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException($"Unable to locate required asset '{relative}'.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary directory left behind is harmless.
        }
    }
}
