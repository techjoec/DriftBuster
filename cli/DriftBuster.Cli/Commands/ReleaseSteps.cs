using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>The release steps run for real: <c>dotnet</c> and <c>bash</c> through <paramref name="launcher"/>, with the checkout at <paramref name="root"/>.</summary>
internal sealed class ReleaseSteps(string root, TextWriter stdout, Func<IReadOnlyList<string>, string?, int> launcher) : IReleaseSteps
{
    /// <summary>The test projects <see cref="RunTests"/> runs, relative to the repository root.</summary>
    public static readonly IReadOnlyList<string> TestProjects =
    [
        "gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj",
        "cli/DriftBuster.Cli.Tests/DriftBuster.Cli.Tests.csproj",
        "gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj",
    ];

    private void Run(IReadOnlyList<string> command, string? cwd = null) => ToolProcess.Run(command, cwd, "→", stdout, launcher);

    public void CleanArtifacts()
    {
        var buildRoot = Path.Combine(root, "build");
        if (TextModeFile.Exists(buildRoot))
        {
            Directory.Delete(buildRoot, recursive: true);
        }

        Directory.CreateDirectory(ReleaseBuild.CliArtifactDir(root));
        Directory.CreateDirectory(ReleaseBuild.GuiArtifactDir(root));
    }

    public void RunTests(bool skipTests)
    {
        if (skipTests)
        {
            ConsoleText.Print(stdout, "Skipping tests as requested.");
            return;
        }

        foreach (var project in TestProjects)
        {
            Run(["dotnet", "test", project, "-c", "Release"]);
        }
    }

    public void BuildCli(string? runtime, bool selfContained)
        => Publish("cli/DriftBuster.Cli/DriftBuster.Cli.csproj", ReleaseBuild.CliArtifactDir(root), runtime, selfContained);

    public void BuildGui(string? runtime, bool selfContained)
        => Publish("gui/DriftBuster.Gui/DriftBuster.Gui.csproj", ReleaseBuild.GuiArtifactDir(root), runtime, selfContained);

    private void Publish(string project, string artifactDir, string? runtime, bool selfContained)
    {
        var targetDir = Path.Combine(artifactDir, ReleaseBuild.RuntimeFolder(runtime));
        Directory.CreateDirectory(targetDir);
        List<string> command = ["dotnet", "publish", project, "-c", "Release", "-o", targetDir];
        if (!string.IsNullOrEmpty(runtime))
        {
            command.AddRange(["-r", runtime, "--self-contained", selfContained ? "true" : "false"]);
        }

        Run(command);
    }

    public void BuildInstaller(string rid, string releaseNotes, string? channel, string? packId)
    {
        if (!TextModeFile.Exists(releaseNotes))
        {
            throw new CommandExitException($"Release notes not found: {PythonPurePath.Str(releaseNotes)}");
        }

        var versions = RunProfileStore.ReadJson(Path.Combine(root, "versions.json"));
        var guiVersion = versions is IReadOnlyDictionary<string, object?> mapping && mapping.TryGetValue("gui", out var gui)
            ? PythonRepr.Str(gui)
            : PythonRepr.Str(PythonBuiltins.Get(versions, "gui") ?? "0.0.0");
        var script = Path.Combine(root, "scripts", "build_velopack_release.sh");
        if (!TextModeFile.Exists(script))
        {
            throw new CommandExitException($"Installer script not found: {script}");
        }

        List<string> command = ["bash", script, "--version", guiVersion, "--rid", rid, "--release-notes", PythonPurePath.Str(releaseNotes)];
        if (!string.IsNullOrEmpty(channel))
        {
            command.AddRange(["--channel", channel]);
        }

        if (!string.IsNullOrEmpty(packId))
        {
            command.AddRange(["--pack-id", packId]);
        }

        Run(command);
    }

    public void StageLocalPortableDev(string rid)
    {
        ConsoleText.Print(stdout, $"\n→ driftbuster release stage-portable --rid {rid} --stage-dir {StagePortable.DefaultStageDir}");
        var options = new StagePortableOptions { Rid = rid, StageDir = StagePortable.DefaultStageDir };
        StagePortable.Run(options, root, Environment.CurrentDirectory, stdout, (repo, configuration, runtime) => StagePortable.BuildPublish(repo, configuration, runtime, stdout, launcher));
    }
}
