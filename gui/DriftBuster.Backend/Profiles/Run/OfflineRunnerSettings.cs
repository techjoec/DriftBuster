namespace DriftBuster.Backend.Profiles.Run;

/// <summary>How the runner packages what it collected.</summary>
public sealed record OfflineRunnerSettings(
    bool Compress,
    bool IncludeConfig,
    bool IncludeLogs,
    bool IncludeManifest,
    string ManifestName,
    string LogName,
    string DataDirectoryName,
    string LogsDirectoryName,
    string PackageName,
    bool CleanupStaging);
