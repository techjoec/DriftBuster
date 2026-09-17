namespace DriftBuster.Cli.Commands;

/// <summary>The steps <see cref="ReleaseBuild.Run"/> runs in order.</summary>
internal interface IReleaseSteps
{
    void CleanArtifacts();

    void RunTests(bool skipTests);

    void BuildCli(string? runtime, bool selfContained);

    void BuildGui(string? runtime, bool selfContained);

    void BuildInstaller(string rid, string releaseNotes, string? channel, string? packId);

    void StageLocalPortableDev(string rid);
}
