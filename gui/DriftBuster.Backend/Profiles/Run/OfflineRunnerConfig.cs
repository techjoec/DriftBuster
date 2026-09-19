namespace DriftBuster.Backend.Profiles.Run;

/// <summary>The config <c>scripts/driftbuster-offline-runner.ps1</c> reads (<see cref="SchemaId"/>).</summary>
public sealed record OfflineRunnerConfig(
    string Schema,
    string Version,
    OfflineRunnerProfile Profile,
    OfflineRunnerSettings Runner,
    IReadOnlyDictionary<string, string> Metadata)
{
    public const string SchemaId = "https://driftbuster.dev/offline-runner/config/v1";
}
