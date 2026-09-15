using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>The runner script packaged beside the config.</summary>
    public const string ScriptFileName = "driftbuster-offline-runner.ps1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

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
            return new OfflineCollectorResult
            {
                PackagePath = packagePath,
                ConfigFileName = configFileName,
                ScriptFileName = ScriptFileName,
            };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// The offline runner config for <paramref name="profile"/>. Each source is an object with <c>path</c>, <c>alias</c> when set,
    /// <c>optional</c> and <c>exclude</c>, the shape the PowerShell runner reads; the baseline defaults to the first source's path.
    /// </summary>
    public static OrderedDictionary<string, object?> BuildConfigPayload(RunProfileDefinition profile, IDictionary<string, string>? metadata, JsonElement secretRules)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var sources = (profile.Sources ?? []).Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Path)).Select(SourcePayload).ToList();
        var baseline = string.IsNullOrWhiteSpace(profile.Baseline) && sources.Count > 0 ? (string?)sources[0]["path"] : profile.Baseline;
        var secretScanner = profile.SecretScanner ?? new SecretScannerOptions();
        var profilePayload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = profile.Name,
            ["description"] = profile.Description,
            ["baseline"] = baseline,
            ["sources"] = sources,
            ["tags"] = new[] { "offline" },
            ["options"] = profile.Options,
            ["secret_scanner"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ignore_rules"] = secretScanner.IgnoreRules ?? [],
                ["ignore_patterns"] = secretScanner.IgnorePatterns ?? [],
                ["ruleset"] = secretRules,
            },
        };
        foreach (var key in profilePayload.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
        {
            profilePayload.Remove(key);
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = "https://driftbuster.dev/offline-runner/config/v1",
            ["version"] = "1",
            ["profile"] = profilePayload,
            ["runner"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["compress"] = true,
                ["include_config"] = true,
                ["include_logs"] = true,
                ["include_manifest"] = true,
                ["manifest_name"] = "manifest.json",
                ["log_name"] = "runner.log",
                ["data_directory_name"] = "data",
                ["logs_directory_name"] = "logs",
                ["package_name"] = $"{RunProfileStore.SafeName(profile.Name)}-offline-results",
                ["cleanup_staging"] = true,
            },
            ["metadata"] = BuildMetadata(profile.Name, metadata),
        };
    }

    private static OrderedDictionary<string, object?> SourcePayload(RunProfileSource source)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = source.Path };
        if (!string.IsNullOrWhiteSpace(source.Alias))
        {
            entry["alias"] = source.Alias;
        }

        entry["optional"] = source.Optional;
        entry["exclude"] = source.Exclude ?? [];
        return entry;
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
        var payload = BuildConfigPayload(profile, metadata, LoadSecretRules(baseDir));
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        File.WriteAllText(configPath, json + Environment.NewLine);

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

    // The profile with blank sources dropped, paths and aliases trimmed and the ignore lists trimmed and deduplicated. Exclude patterns are
    // kept as written, as the run executor matches them, so a profile excludes the same files when run and when collected.
    private static RunProfileDefinition CloneProfile(RunProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new RunProfileDefinition
        {
            Name = profile.Name,
            Description = profile.Description,
            Baseline = profile.Baseline,
            Sources = (profile.Sources ?? [])
                .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Path))
                .Select(source => new RunProfileSource(source.Path.Trim())
                {
                    Alias = string.IsNullOrWhiteSpace(source.Alias) ? null : source.Alias.Trim(),
                    Optional = source.Optional,
                    Exclude = [.. source.Exclude ?? []],
                })
                .ToArray(),
            Options = new Dictionary<string, string>(profile.Options ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal),
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = CleanList(profile.SecretScanner?.IgnoreRules),
                IgnorePatterns = CleanList(profile.SecretScanner?.IgnorePatterns),
            },
        };
    }

    private static string[] CleanList(string[]? values)
        => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();

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

    private static OrderedDictionary<string, object?> BuildMetadata(string profileName, IDictionary<string, string>? metadata)
    {
        var meta = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile_name"] = profileName,
            ["prepared_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
        {
            meta["prepared_by"] = user;
        }

        foreach (var entry in metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                meta[entry.Key.Trim()] = entry.Value ?? string.Empty;
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
