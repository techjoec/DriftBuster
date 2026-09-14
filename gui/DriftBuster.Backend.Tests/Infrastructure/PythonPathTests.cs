using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonPath.IsFile"/> is <c>Path.is_file()</c>: a <c>stat</c> that follows links must say regular file.</summary>
public sealed class PythonPathTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-python-path-");

    public void Dispose() => SpecialFiles.DeleteTree(_tmp);

    [Fact]
    public void RegularFilesAndLinksToThemAreFiles()
    {
        var regular = Path.Combine(_tmp.FullName, "a.ini");
        File.WriteAllText(regular, "x");
        PythonPath.IsFile(regular).Should().BeTrue();
        PythonPath.IsFile(_tmp.FullName).Should().BeFalse();
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "missing")).Should().BeFalse();
        PythonPath.IsFile(regular + "\0tail").Should().BeFalse();
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "link.ini"), "a.ini");
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "dangling.ini"), "nowhere");
            PythonPath.IsFile(Path.Combine(_tmp.FullName, "link.ini")).Should().BeTrue();
            PythonPath.IsFile(Path.Combine(_tmp.FullName, "dangling.ini")).Should().BeFalse();
            PythonPath.IsFile(regular + "/").Should().BeFalse("stat of a file spelled as a directory fails with ENOTDIR");
        }
    }

    [Fact]
    public void FifosSocketsAndDevicesAreNotFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and Unix sockets in a walked tree are a Linux case");
        using var special = SpecialFiles.Create(_tmp.FullName);

        foreach (var path in special.All)
        {
            PythonPath.IsFile(path).Should().BeFalse(path);
        }

        PythonPath.IsFile("/dev/null").Should().BeFalse();
    }

    [Fact]
    public void AnUndecodableNameIsRecognisedWithoutOpeningIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        SpecialFiles.CreateUndecodableName(_tmp.FullName, "server host=bad.corp.local\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "good-\uFFFD.ini"), "x");

        var listed = Directory.GetFiles(_tmp.FullName).Order(StringComparer.Ordinal).ToList();

        listed.Should().HaveCount(2);
        listed.Where(PythonPath.IsUndecodableName).Select(Path.GetFileName).Should().Equal("bad-\uFFFD.ini");
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "good-\uFFFD.ini")).Should().BeTrue();
    }

    // CPython 3.13 Path.is_file() ignores only ENOENT, ENOTDIR, EBADF and ELOOP (pathlib._abc._IGNORED_ERRNOS); a file inside a
    // directory that can be listed but not searched raises PermissionError: [Errno 13] Permission denied.
    [Fact]
    public void AStatThatFailsWithAnErrorPythonDoesNotIgnoreRaises()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "search permission is observable only for a non-root Linux user");
        var file = SpecialFiles.CreateUnsearchableDirectory(_tmp.FullName, "rdir", "alpha one\n");

        var isFile = () => PythonPath.IsFile(file);
        var undecodable = () => PythonPath.IsUndecodableName(Path.Combine(_tmp.FullName, "rdir", "bad-\uFFFD.ini"));

        isFile.Should().Throw<UnauthorizedAccessException>().WithMessage($"[Errno 13] Permission denied: '{file}'");
        undecodable.Should().Throw<UnauthorizedAccessException>();
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "rdir")).Should().BeFalse();
        var tooLong = () => PythonPath.IsFile(Path.Combine(_tmp.FullName, new string('n', 300)));
        tooLong.Should().Throw<IOException>().WithMessage("[Errno 36] File name too long: '*'");
    }

    // tree/shortcut -> real/app/nested: tree/shortcut/.. is tree/real/app to the kernel and tree to a lexical collapse.
    private string DotDotTree()
    {
        var tree = Path.Combine(_tmp.FullName, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "real", "app", "nested"));
        File.WriteAllText(Path.Combine(tree, "real", "app", "x.txt"), "physical");
        File.WriteAllText(Path.Combine(tree, "x.txt"), "lexical");
        File.WriteAllText(Path.Combine(tree, "file.txt"), "a file");
        Directory.CreateSymbolicLink(Path.Combine(tree, "shortcut"), "real/app/nested");
        return tree;
    }

    [Fact]
    public void KernelPathStepsToTheParentOfWhereALinkLeads()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = DotDotTree();

        PythonPath.KernelPath($"{tree}/shortcut/../x.txt").Should().Be(Path.Combine(tree, "real", "app", "x.txt"));
        File.ReadAllText(PythonPath.KernelPath($"{tree}/shortcut/../x.txt")).Should().Be("physical");
        PythonPath.KernelPath($"{tree}/shortcut/..").Should().Be(Path.Combine(tree, "real", "app"));
        PythonPath.KernelPath($"{tree}/shortcut/../../../x.txt").Should().Be(Path.Combine(tree, "x.txt"));
        PythonPath.KernelPath($"{tree}/real/../x.txt").Should().Be(Path.Combine(tree, "x.txt"));
        PythonPath.KernelPath($"{tree}/shortcut/x.txt").Should().Be($"{tree}/shortcut/x.txt", "a path without '..' is left to the runtime");
        PythonPath.KernelPath("/../..").Should().Be("/");
    }

    [Fact]
    public void KernelPathThroughAPartThatIsNotADirectoryReachesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = DotDotTree();
        File.CreateSymbolicLink(Path.Combine(tree, "loop"), "loop");

        foreach (var spelled in new[] { $"{tree}/missing/../x.txt", $"{tree}/file.txt/../x.txt", $"{tree}/loop/../x.txt", $"{tree}/file.txt/.." })
        {
            var kernel = PythonPath.KernelPath(spelled);
            File.Exists(kernel).Should().BeFalse(spelled);
            Directory.Exists(kernel).Should().BeFalse(spelled);
            PythonPath.IsFile(spelled).Should().BeFalse(spelled);
        }
    }

    // links/dotdot -> dotdot/<0xFF>, next to dotdot/x; trap/U+FFFD -> elsewhere/deep, and links/trap -> trap/<0xFF>, next to
    // trap/x. The runtime reads either link target as U+FFFD; the kernel never looks that spelling up.
    private string UndecodableLinkTree()
    {
        var tree = Path.Combine(_tmp.FullName, "undecodable");
        SpecialFiles.Shell(
            "t=\"$1\"; mkdir -p \"$t/dotdot/$ff/sub\" \"$t/dotdot/x\" \"$t/links\" \"$t/trap/$ff\" \"$t/trap/x\" \"$t/elsewhere/deep\" \"$t/elsewhere/x\""
            + " && printf real > \"$t/dotdot/x/app.ini\" && printf real > \"$t/trap/x/app.ini\" && printf guessed > \"$t/elsewhere/x/app.ini\""
            + " && ln -s \"$t/dotdot/$ff\" \"$t/links/dotdot\" && ln -s \"$t/elsewhere/deep\" \"$t/trap/$fffd\" && ln -s \"$t/trap/$ff\" \"$t/links/trap\""
            + " && ln -s \"$ff/sub\" \"$t/dotdot/deeper\"",
            tree);
        return tree;
    }

    [Fact]
    public void KernelPathNeverFollowsTheReplacementSpellingOfALinkTargetThatIsNotUtf8()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "link targets are bytes only on Linux here");
        var tree = UndecodableLinkTree();

        PythonPath.KernelPath($"{tree}/links/dotdot/../x/app.ini").Should().Be($"{tree}/dotdot/x/app.ini");
        File.ReadAllText(PythonPath.KernelPath($"{tree}/links/dotdot/../x/app.ini")).Should().Be("real");
        PythonPath.KernelPath($"{tree}/links/trap/../x/app.ini").Should().Be($"{tree}/trap/x/app.ini");
        File.ReadAllText(PythonPath.KernelPath($"{tree}/links/trap/../x/app.ini")).Should().Be("real", "trap/U+FFFD leads to elsewhere, which holds 'guessed'");
        PythonPath.KernelPath($"{tree}/links/dotdot/sub/../../x").Should().Be($"{tree}/dotdot/x", "sub is no link: its '..' is removed without naming dotdot/<0xFF>");
        PythonPath.KernelPath($"{tree}/links/trap/../x/..").Should().Be($"{tree}/trap");
    }

    [Fact]
    public void KernelPathOfADirectoryWithoutAUtf8NameNamesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "link targets are bytes only on Linux here");
        var tree = UndecodableLinkTree();

        // dotdot/deeper -> <0xFF>/sub: the kernel reaches dotdot/<0xFF>, which no string names.
        foreach (var spelled in new[] { $"{tree}/dotdot/deeper/..", $"{tree}/dotdot/deeper/../sub/../x.ini", $"{tree}/dotdot/deeper/../missing/../x" })
        {
            var kernel = PythonPath.KernelPath(spelled);
            kernel.Should().StartWith(PythonPath.Unreachable, spelled);
            Directory.Exists(kernel).Should().BeFalse(spelled);
            File.Exists(kernel).Should().BeFalse(spelled);
        }

        // Without a '..' the kernel follows the link itself, and a '..' that only leaves a real directory below the link keeps it.
        PythonPath.KernelPath($"{tree}/dotdot/deeper").Should().Be($"{tree}/dotdot/deeper");
        PythonPath.KernelPath($"{tree}/links/dotdot/sub/..").Should().Be($"{tree}/links/dotdot");
        Directory.Exists(PythonPath.KernelPath($"{tree}/links/dotdot/sub/..")).Should().BeTrue();
    }

    [Fact]
    public void ResolvePhysicalPathReadsLinkTargetsAsBytes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "link targets are bytes only on Linux here");
        var tree = UndecodableLinkTree();
        var physical = PythonPath.ResolvePhysicalPath(tree)!;

        PythonPath.ResolvePhysicalPath($"{tree}/links/trap", out var nameable).Should().Be($"{physical}/trap/\uFFFD");
        nameable.Should().BeFalse();
        PythonPath.ResolvePhysicalPath($"{tree}/links/trap/x", out nameable).Should().Be($"{physical}/trap/\uFFFD/x", "trap/U+FFFD is a link to elsewhere/deep, never followed");
        nameable.Should().BeFalse();
        PythonPath.ResolvePhysicalPath($"{tree}/links/trap/../x", out nameable).Should().Be($"{physical}/trap/x");
        nameable.Should().BeTrue();
        PythonPath.ResolvePhysicalPath($"{tree}/trap/\uFFFD/x", out nameable).Should().Be($"{physical}/elsewhere/deep/x", "a real U+FFFD name is UTF-8 and followed");
        nameable.Should().BeTrue();
    }

    [Fact]
    public void KernelPathJoinsARelativePathOntoTheWorkingDirectoryFirst()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = DotDotTree();
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), tree);

        PythonPath.Absolute($"{relative}/shortcut/..").Should().Be($"{Directory.GetCurrentDirectory()}/{relative}/shortcut/..");
        PythonPath.Absolute(".").Should().Be(Directory.GetCurrentDirectory());
        File.ReadAllText(PythonPath.KernelPath($"{relative}/shortcut/../x.txt")).Should().Be("physical");
    }

    [Fact]
    public void SortedGlobListsTheDirectoryTheKernelReaches()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var tree = DotDotTree();

        PythonPath.SortedGlob($"{tree}/shortcut/..", "**/*", TestContext.Current.CancellationToken).Should().Equal($"{tree}/shortcut/../nested", $"{tree}/shortcut/../x.txt");
    }
}
