using System.CommandLine;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster release</c> (<see cref="ReleaseBuild"/>) and <c>driftbuster release stage-portable</c> (<see cref="StagePortable"/>), both
/// run from the root of the checkout above the tool.
/// </summary>
internal static class ReleaseCommand
{
    public static Command Build()
    {
        var skipTests = PythonArguments.Flag("--skip-tests", "Skip running dotnet test before building artifacts.");
        var runtime = PythonArguments.OptionalText("--runtime", "Optional runtime identifier for dotnet publish (e.g., win-x64).");
        var frameworkDependent = PythonArguments.Flag(
            "--framework-dependent", "Publish without the .NET runtime when a runtime is specified (default: self-contained).");
        var noInstaller = PythonArguments.Flag("--no-installer", "Do not build a Velopack installer (default builds installer).");
        var installerRid = PythonArguments.Text("--installer-rid", "win-x64", "RID for installer packaging (default: win-x64).");
        var releaseNotes = PythonArguments.OptionalText("--release-notes", "Path to release notes markdown (required for installer packaging).");
        var channel = PythonArguments.OptionalText("--channel", "Optional installer update channel label.");
        var packId = PythonArguments.OptionalText("--pack-id", "Override installer pack id (defaults to com.driftbuster.gui).");
        var command = new Command("release", "Prepare DriftBuster release artifacts.")
        {
            skipTests, runtime, frameworkDependent, noInstaller, installerRid, releaseNotes, channel, packId, BuildStagePortable(),
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var options = new ReleaseOptions
            {
                SkipTests = parseResult.GetValue(skipTests),
                Runtime = parseResult.GetValue(runtime),
                FrameworkDependent = parseResult.GetValue(frameworkDependent),
                NoInstaller = parseResult.GetValue(noInstaller),
                InstallerRid = parseResult.GetValue(installerRid)!,
                ReleaseNotes = parseResult.GetValue(releaseNotes),
                Channel = parseResult.GetValue(channel),
                PackId = parseResult.GetValue(packId),
            };
            var root = RepositoryRoot.Require();
            return ReleaseBuild.Run(options, root, Environment.CurrentDirectory, new ReleaseSteps(root, stdout, ToolProcess.Launch), stdout);
        }));
        return command;
    }

    private static Command BuildStagePortable()
    {
        var stageDir = PythonArguments.Text("--stage-dir", StagePortable.DefaultStageDir, $"Local stage directory (default: {StagePortable.DefaultStageDir}).");
        var rid = PythonArguments.Text("--rid", "win-x64", "dotnet publish runtime identifier (default: win-x64).");
        var configuration = PythonArguments.Text("--configuration", "Release", "dotnet publish configuration (default: Release).");
        var timestamp = PythonArguments.OptionalText("--timestamp", "Optional UTC timestamp override (format: YYYYMMDD-HHMMSSZ).");
        var command = new Command("stage-portable", "Build a portable debug bundle and stage it for local Win11 testing.")
        {
            stageDir, rid, configuration, timestamp,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var options = new StagePortableOptions
            {
                StageDir = parseResult.GetValue(stageDir)!,
                Rid = parseResult.GetValue(rid)!,
                Configuration = parseResult.GetValue(configuration)!,
                Timestamp = parseResult.GetValue(timestamp),
            };
            var root = RepositoryRoot.Require();
            return StagePortable.Run(
                options,
                root,
                Environment.CurrentDirectory,
                stdout,
                (repo, config, runtime) => StagePortable.BuildPublish(repo, config, runtime, stdout, ToolProcess.Launch));
        }));
        return command;
    }
}
