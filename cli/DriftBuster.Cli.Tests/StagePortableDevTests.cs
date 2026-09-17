using System.IO.Compression;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <see cref="StagePortable"/> (<c>driftbuster release stage-portable</c>). The bundle holds only the published GUI, so the bundle test
/// asserts no <c>src</c>.
/// </summary>
public sealed class StagePortableDevTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-stage-portable-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_tmp.FullName, .. parts])).FullName;

    [Fact]
    public void StageBundleReplacesExistingDirectory()
    {
        var bundleDir = Dir("bundle");
        File.WriteAllText(Path.Combine(bundleDir, "new.txt"), "new");
        var stageDir = Dir("stage");
        File.WriteAllText(Path.Combine(stageDir, "old.txt"), "old");

        StagePortable.StageBundle(bundleDir, stageDir);

        File.Exists(Path.Combine(stageDir, "old.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(stageDir, "new.txt")).Should().Be("new");
    }

    [Fact]
    public void TryWriteLaunchersCreatesExpectedFiles()
    {
        var bundleDir = Dir("bundle");

        StagePortable.WriteDebugLaunchers(bundleDir);

        File.Exists(Path.Combine(bundleDir, "Run-DriftBuster-Debug.cmd")).Should().BeTrue();
        File.Exists(Path.Combine(bundleDir, "Run-DriftBuster-Debug.ps1")).Should().BeTrue();
        File.Exists(Path.Combine(bundleDir, "README.debug.txt")).Should().BeTrue();
    }

    [Fact]
    public void MainBuildsBundleAndStagesToTarget()
    {
        var root = _tmp.FullName;
        var publishDir = Dir("gui", "DriftBuster.Gui", "bin", "Release", "net10.0", "win-x64", "publish");
        File.WriteAllText(Path.Combine(publishDir, "DriftBuster.Gui.exe"), "binary");
        File.WriteAllText(Path.Combine(root, "versions.json"), "{\"gui\": \"0.1.0\"}");
        var stageDir = Dir("staged");
        File.WriteAllText(Path.Combine(stageDir, "stale.txt"), "stale");
        var options = new StagePortableOptions { StageDir = stageDir, Rid = "win-x64", Configuration = "Release", Timestamp = "20260305-000000Z" };

        var result = StagePortable.Run(options, root, root, TextWriter.Null, (_, _, _) => publishDir);

        result.Should().Be(0);
        File.Exists(Path.Combine(stageDir, "stale.txt")).Should().BeFalse();
        File.Exists(Path.Combine(stageDir, "DriftBuster.Gui.exe")).Should().BeTrue();
        File.Exists(Path.Combine(stageDir, "Run-DriftBuster-Debug.cmd")).Should().BeTrue();
        Directory.Exists(Path.Combine(stageDir, "src")).Should().BeFalse();
        var zipPath = Path.Combine(root, "artifacts", "gui-packaging", "portable", "DriftBuster.Gui-0.1.0-win11-portable-debug-20260305-000000Z.zip");
        File.Exists(zipPath).Should().BeTrue();
        using var archive = ZipFile.OpenRead(zipPath);
        archive.Entries.Select(entry => entry.FullName).Should().StartWith("DriftBuster.Gui-0.1.0-win11-portable-debug-20260305-000000Z/");
    }

    [Fact]
    public void MainRequiresRepoRoot()
    {
        var act = () => StagePortable.Run(new StagePortableOptions(), _tmp.FullName, Path.Combine(_tmp.FullName, "child"), TextWriter.Null, (_, _, _) => "");

        act.Should().Throw<CommandExitException>().WithMessage("Run this script from repository root*");
    }

    [Fact]
    public void StageBundleFallsBackWhenExistingExeIsLocked()
    {
        var bundleDir = Dir("bundle");
        File.WriteAllText(Path.Combine(bundleDir, "DriftBuster.Gui.exe"), "new-binary");
        StagePortable.WriteDebugLaunchers(bundleDir);
        var stageDir = Dir("stage");
        File.WriteAllText(Path.Combine(stageDir, "DriftBuster.Gui.exe"), "old-binary");
        var ops = new PortableFileOps(
            _ => throw new UnauthorizedAccessException("locked"),
            (source, destination) =>
            {
                if (string.Equals(Path.GetFileName(destination), "DriftBuster.Gui.exe", StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("busy");
                }

                PortableFileOps.CopyWithTimes(source, destination);
            });

        StagePortable.StageBundle(bundleDir, stageDir, ops);

        File.Exists(Path.Combine(stageDir, "DriftBuster.Gui.next.exe")).Should().BeTrue();
        File.ReadAllText(Path.Combine(stageDir, "Run-DriftBuster-Debug.cmd")).Should().Contain("DriftBuster.Gui.next.exe");
    }
}
