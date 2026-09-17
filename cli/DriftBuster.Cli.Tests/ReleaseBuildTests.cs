using DriftBuster.Backend.Infrastructure;
using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <see cref="ReleaseBuild"/> and <see cref="ReleaseSteps"/> (<c>driftbuster release</c>): the console tool and GUI publishes, the .NET test
/// projects, and a missing executable.
/// </summary>
public sealed class ReleaseBuildTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-release-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private sealed class RecordingSteps : IReleaseSteps
    {
        public List<object> Calls { get; } = [];

        public void CleanArtifacts() => Calls.Add("clean");

        public void RunTests(bool skipTests) => Calls.Add(("tests", skipTests));

        public void BuildCli(string? runtime, bool selfContained) => Calls.Add("cli");

        public void BuildGui(string? runtime, bool selfContained) => Calls.Add(("gui", runtime, selfContained));

        public void BuildInstaller(string rid, string releaseNotes, string? channel, string? packId) => Calls.Add("installer");

        public void StageLocalPortableDev(string rid) => Calls.Add(("stage", rid));
    }

    [Fact]
    public void MissingToolRaisesForMissing()
    {
        var act = () => ToolProcess.Launch(["missing_module"], null);

        act.Should().Throw<IOException>().WithMessage("[Errno 2] No such file or directory: 'missing_module'");
    }

    [Fact]
    public void CleanArtifactsResetsDirectories()
    {
        var cliDir = Path.Combine(_tmp.FullName, "build", "artifacts", "cli");
        var guiDir = Path.Combine(_tmp.FullName, "build", "artifacts", "gui");
        Directory.CreateDirectory(cliDir);
        File.WriteAllText(Path.Combine(cliDir, "placeholder.txt"), "data");

        new ReleaseSteps(_tmp.FullName, TextWriter.Null, (_, _) => 0).CleanArtifacts();

        Directory.Exists(cliDir).Should().BeTrue();
        Directory.Exists(guiDir).Should().BeTrue();
        File.Exists(Path.Combine(cliDir, "placeholder.txt")).Should().BeFalse();
    }

    [Fact]
    public void RunTestsHonoursSkip()
    {
        var commands = new List<(IReadOnlyList<string> Command, string? Cwd)>();
        var exitCode = 0;
        var steps = new ReleaseSteps(_tmp.FullName, TextWriter.Null, (command, cwd) =>
        {
            commands.Add((command, cwd));
            return exitCode;
        });

        steps.RunTests(skipTests: true);
        commands.Should().BeEmpty();

        steps.RunTests(skipTests: false);
        commands[0].Command.Take(3).Should().Equal("dotnet", "test", ReleaseSteps.TestProjects[0]);
        exitCode = 1;
        var act = () => steps.RunTests(skipTests: false);
        act.Should().Throw<CalledProcessException>();
    }

    [Fact]
    public void MainExecutesPipeline()
    {
        var steps = new RecordingSteps();

        var result = ReleaseBuild.Run(new ReleaseOptions { SkipTests = false, NoInstaller = true }, _tmp.FullName, _tmp.FullName, steps, TextWriter.Null);

        result.Should().Be(0);
        steps.Calls.Should().Equal("clean", ("tests", false), "cli", ("gui", (string?)null, true), ("stage", "win-x64"));
    }

    [Fact]
    public void MainSkipsLocalPortableStageForNonWindowsRuntime()
    {
        var steps = new RecordingSteps();
        var options = new ReleaseOptions { SkipTests = true, Runtime = "linux-x64", NoInstaller = true };

        var result = ReleaseBuild.Run(options, _tmp.FullName, _tmp.FullName, steps, TextWriter.Null);

        result.Should().Be(0);
        steps.Calls.Should().Equal("clean", ("tests", true), "cli", ("gui", (string?)"linux-x64", true));
    }

    [Fact]
    public void MainRequiresRepositoryRoot()
    {
        var act = () => ReleaseBuild.Run(new ReleaseOptions(), _tmp.FullName, Path.Combine(_tmp.FullName, "child"), new RecordingSteps(), TextWriter.Null);

        act.Should().Throw<CommandExitException>().WithMessage("Run this script from the repository root*");
    }
}
