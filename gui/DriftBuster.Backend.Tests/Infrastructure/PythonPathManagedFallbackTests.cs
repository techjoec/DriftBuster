using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// The managed resolution <see cref="PythonPath.KernelPath"/>, <see cref="PythonPath.ResolvePhysicalPath(string)"/> and <see cref="PythonPath.MakeDirectories"/> fall back
/// to where no byte walk exists (Windows for <c>ResolvePhysicalPath</c>, Unix systems without the <c>readlink</c> and <c>statx</c>
/// bindings), run on Linux through <see cref="UnixPathWalk.Disabled"/>. The seam is process-wide, so the class runs alone.
/// </summary>
[Collection(ProcessWideSeamCollection.Name)]
public sealed class PythonPathManagedFallbackTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-python-path-fallback-");

    public PythonPathManagedFallbackTests() => UnixPathWalk.Disabled = true;

    public void Dispose()
    {
        UnixPathWalk.Disabled = false;
        SpecialFiles.DeleteTree(_tmp);
    }

    // tree/shortcut -> real/app/nested, a file, a loop and a relative link to a file through a parent step.
    private string Tree()
    {
        var tree = Path.Combine(_tmp.FullName, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "real", "app", "nested"));
        File.WriteAllText(Path.Combine(tree, "real", "app", "x.txt"), "physical");
        File.WriteAllText(Path.Combine(tree, "x.txt"), "lexical");
        File.WriteAllText(Path.Combine(tree, "file.txt"), "a file");
        Directory.CreateSymbolicLink(Path.Combine(tree, "shortcut"), "real/app/nested");
        File.CreateSymbolicLink(Path.Combine(tree, "loop"), "loop");
        File.CreateSymbolicLink(Path.Combine(tree, "real", "rel.txt"), "../file.txt");
        return tree;
    }

    [Fact]
    public void KernelPathWalksLinksAndParentStepsWithoutTheByteWalk()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = Tree();

        PythonPath.KernelPath($"{tree}/shortcut/../x.txt").Should().Be(Path.Combine(tree, "real", "app", "x.txt"));
        PythonPath.KernelPath($"{tree}/shortcut/../../../x.txt").Should().Be(Path.Combine(tree, "x.txt"));
        PythonPath.KernelPath("/../..").Should().Be("/");
        foreach (var spelled in new[] { $"{tree}/missing/../x.txt", $"{tree}/file.txt/../x.txt", $"{tree}/loop/../x.txt" })
        {
            var kernel = PythonPath.KernelPath(spelled);
            File.Exists(kernel).Should().BeFalse(spelled);
            Directory.Exists(kernel).Should().BeFalse(spelled);
        }
    }

    [Fact]
    public void MakeDirectoriesStepsOutOfADirectoryItHasJustCreatedWithoutTheByteWalk()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = Tree();

        PythonPath.MakeDirectories($"{tree}/new/../sub/leaf");
        PythonPath.MakeDirectories($"{tree}/shortcut/../made");

        Directory.Exists(Path.Combine(tree, "new")).Should().BeTrue();
        Directory.Exists(Path.Combine(tree, "sub", "leaf")).Should().BeTrue();
        Directory.Exists(Path.Combine(tree, "real", "app", "made")).Should().BeTrue();
        Directory.Exists(Path.Combine(tree, "made")).Should().BeFalse();
    }

    [Fact]
    public void ResolvePhysicalPathFollowsRelativeTargetsAndReportsLoopsWithoutTheByteWalk()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symlinks are created without privileges only on Unix here");
        var tree = Tree();
        var physical = Path.GetFullPath(tree);

        PythonPath.ResolvePhysicalPath($"{tree}/real/rel.txt", out var nameable).Should().Be(Path.Combine(physical, "file.txt"));
        nameable.Should().BeTrue();
        PythonPath.ResolvePhysicalPath($"{tree}/shortcut/./../missing").Should().Be(Path.Combine(physical, "real", "app", "missing"));
        PythonPath.ResolvePhysicalPath($"{tree}/loop").Should().BeNull();
        PythonPath.IsFile($"{tree}/real/rel.txt").Should().BeTrue();
    }
}
